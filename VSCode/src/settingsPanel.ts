import { randomBytes } from "crypto";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { AsmRequest, NcmActions } from "./actions";
import { stripAnsi } from "./ansi";
import { Configuration } from "./config";
import { CreateValues, ProjectCreator } from "./creator";
import { I18n } from "./i18n";
import { Log } from "./log";
import { Ncm } from "./ncm";
import { NuGetCache } from "./nugetCache";
import { panelMarkup } from "./panelMarkup";
import { ProjectLocator } from "./project";

//DiagnosticRow one problem the language server reported, as the page lists it
interface DiagnosticRow {
    file: string;
    display: string;
    line: number;
    column: number;
    severity: "error" | "warning";
    message: string;
}

//SettingsPanel hosts the extension page in a main editor tab
//One panel is reused, so pressing the entry icon again reveals the open tab instead of stacking another one
export class SettingsPanel {
    private static current: vscode.WebviewPanel | undefined;

    //set when the page is asked for with the create dialog already open, consumed once the page reports ready
    private static pendingCreate = false;

    public static show(context: vscode.ExtensionContext, create = false): void {
        //a pack dropped into the extension folder while it runs becomes selectable when the page opens
        I18n.refreshLanguages(context);
        SettingsPanel.pendingCreate = SettingsPanel.pendingCreate || create;

        if (SettingsPanel.current) {
            SettingsPanel.current.reveal(vscode.ViewColumn.One);
            SettingsPanel.push();
            SettingsPanel.openCreateIfPending();
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            SettingsPanel.viewType,
            "NacreCraft",
            vscode.ViewColumn.One,
            { enableScripts: true, retainContextWhenHidden: true }
        );

        //the tab carries the extension mark instead of the generic webview icon, one drawing per theme since the
        //activity bar version takes its color from the surrounding chrome and a tab icon cannot
        panel.iconPath = {
            light: vscode.Uri.joinPath(
                context.extensionUri,
                "media",
                "nacrecraft-light.svg"
            ),
            dark: vscode.Uri.joinPath(
                context.extensionUri,
                "media",
                "nacrecraft-dark.svg"
            ),
        };

        panel.webview.html = SettingsPanel.html(panel.webview, context);
        panel.webview.onDidReceiveMessage(
            (message) => SettingsPanel.handle(message),
            null,
            context.subscriptions
        );

        //the subscriptions belong to this panel, a reopened page must not push twice
        const listeners: vscode.Disposable[] = [];

        panel.onDidDispose(
            () => {
                for (const listener of listeners) {
                    listener.dispose();
                }
                SettingsPanel.current = undefined;
            },
            null,
            context.subscriptions
        );

        SettingsPanel.current = panel;

        //settings and language go through refresh, which the extension triggers once the language is reloaded
        listeners.push(Log.changed(() => SettingsPanel.pushLog()));
    }

    private static readonly viewType = "nacrecraft.panel";

    //Refresh sends the new pack, the settings and the project again, since all of them were translated before
    public static refresh(): void {
        SettingsPanel.current?.webview.postMessage({
            type: "texts",
            texts: I18n.dump(),
        });
        SettingsPanel.push();
    }

    //RefreshSettings sends the settings list again, which a value dragged out on the page needs
    public static refreshSettings(): void {
        SettingsPanel.current?.webview.postMessage({
            type: "settings",
            items: SettingsPanel.items(),
        });
    }

    //handle one message from the page
    private static handle(message: PageMessage): void {
        if (message.type === "ready") {
            SettingsPanel.push();
            SettingsPanel.pushAsmDefault();
            SettingsPanel.openCreateIfPending();
            return;
        }

        if (message.type === "update" && message.key) {
            Configuration.set(message.key, message.value);
            return;
        }

        //the raw file is opened in the editor rather than rendered here, which keeps the page small
        if (message.type === "openJson") {
            vscode.commands.executeCommand(
                "vscode.open",
                vscode.Uri.file(Configuration.path)
            );
            return;
        }

        if (message.type === "create" && message.values) {
            void SettingsPanel.create(message.values);
            return;
        }

        if (
            message.type === "addRef" &&
            (message.kind === "project" || message.kind === "assembly")
        ) {
            void SettingsPanel.addReference(message.kind);
            return;
        }

        if (message.type === "openDiagnostic" && message.file) {
            void SettingsPanel.openDiagnostic(
                message.file,
                message.line ?? 1,
                message.column ?? 1
            );
            return;
        }

        //the assembly page hands its inspections to the server, which links the asm code directly
        if (message.type === "asm" && message.file && message.mode) {
            void SettingsPanel.asm(message);
            return;
        }

        if (message.type === "asmBrowse") {
            void SettingsPanel.browseAssembly();
            return;
        }

        if (message.type === "asmExtract" && message.file && message.query) {
            void SettingsPanel.extractResource(message.file, message.query);
            return;
        }

        if (message.type === "asmEmbed" && message.file) {
            void SettingsPanel.embedResources(message.file);
            return;
        }

        if (message.type === "asmRemove" && message.file && message.query) {
            void SettingsPanel.removeResource(message.file, message.query);
            return;
        }

        //the package page reads and changes the references through the server, which owns the project file
        if (message.type === "nugetRefs") {
            void SettingsPanel.nugetReferences(message.id);
            return;
        }

        if (message.type === "nugetSet" && message.kind) {
            void SettingsPanel.nugetSet(message);
            return;
        }

        if (message.type === "nugetSearch") {
            void SettingsPanel.nugetSearch(message);
            return;
        }

        if (message.type === "nugetReadme" && message.packageId && message.version) {
            void SettingsPanel.nugetReadme(message);
            return;
        }

        if (message.type === "nugetVersions" && message.packageId) {
            void SettingsPanel.nugetVersions(message);
            return;
        }

        if (message.type === "nugetFocus") {
            SettingsPanel.focusNuGet();
            return;
        }

        //the configuration page reads and writes the project file through the server
        if (message.type === "config") {
            void SettingsPanel.requestConfig();
            return;
        }

        if (message.type === "configSet" && message.key) {
            void SettingsPanel.setConfig(message);
            return;
        }

        //the task page lists and edits the task block, then asks the server to run one
        if (message.type === "tasks") {
            void SettingsPanel.requestTasks();
            return;
        }

        if (message.type === "tasksSaveTask") {
            void SettingsPanel.saveTask(message);
            return;
        }

        if (message.type === "tasksRemove" && message.name) {
            void SettingsPanel.removeTask(message);
            return;
        }

        if (message.type === "tasksOverride") {
            void SettingsPanel.setTasksOverride(message);
            return;
        }

        if (message.type === "taskRun" && message.name) {
            void SettingsPanel.runTask(message.name);
            return;
        }

        //the javascript editor asks the runtime itself, so the names match what a task run would see
        if (message.type === "scriptComplete") {
            void SettingsPanel.scriptComplete(message);
            return;
        }

        if (message.type === "scriptCheck") {
            void SettingsPanel.scriptCheck(message);
        }
    }

    //requestConfig sends the settings the project may declare, which the page lists
    private static async requestConfig(): Promise<void> {
        const result = await NcmActions.config();
        void SettingsPanel.current?.webview.postMessage({
            type: "config",
            ...result,
        });
    }

    //setConfig writes or removes one setting and sends the list that results from it
    private static async setConfig(message: PageMessage): Promise<void> {
        const result = await NcmActions.configSet(
            message.key ?? "",
            message.value ?? "",
            message.remove === true
        );

        void SettingsPanel.current?.webview.postMessage({
            type: "configSet",
            ...result,
        });
    }

    //requestTasks sends what the project declares, which the page lists
    private static async requestTasks(): Promise<void> {
        const result = await NcmActions.tasks();
        void SettingsPanel.current?.webview.postMessage({
            type: "tasks",
            ...result,
        });
    }

    //saveTask replaces one task, or appends a new one when the name is empty
    private static async saveTask(message: PageMessage): Promise<void> {
        const result = await NcmActions.tasksSaveTask(
            message.name ?? "",
            message.xml ?? "",
            message.config ?? ""
        );
        void SettingsPanel.current?.webview.postMessage({
            type: "tasks",
            saved: result.ok,
            ...result,
        });
    }

    //removeTask drops one declared task and answers with what remains
    private static async removeTask(message: PageMessage): Promise<void> {
        const result = await NcmActions.tasksRemove(
            message.name ?? "",
            message.config ?? ""
        );
        void SettingsPanel.current?.webview.postMessage({
            type: "tasks",
            removed: result.ok,
            ...result,
        });
    }

    //setTasksOverride writes the switch that lets a task replace a built-in tool
    private static async setTasksOverride(message: PageMessage): Promise<void> {
        const result = await NcmActions.tasksOverride(message.enabled === true);
        void SettingsPanel.current?.webview.postMessage({
            type: "tasks",
            ...result,
        });
    }

    //runTask starts one task, asking first when it replaces a built-in tool
    //The tool itself refuses to confirm from a pipe, so the question belongs to the editor
    public static async runTask(name: string, approve = false): Promise<void> {
        const result = await NcmActions.taskRun(name, approve);

        if (result.needsApproval) {
            const steps = (result.steps ?? []).join("\n");
            const question =
                I18n.t("tasks.approval").replace("{name}", name) +
                (steps.length > 0 ? "\n\n" + steps : "");

            const choice = await vscode.window.showWarningMessage(
                question,
                { modal: true },
                I18n.t("tasks.approvalRun")
            );

            if (choice) {
                void SettingsPanel.runTask(name, true);
                return;
            }

            //a refused confirmation leaves the page as it was before the click
            void SettingsPanel.current?.webview.postMessage({
                type: "taskStarted",
                name,
                ok: false,
                error: "",
            });
            return;
        }

        void SettingsPanel.current?.webview.postMessage({
            type: "taskStarted",
            name,
            ok: result.ok,
            error: result.error ?? "",
        });
    }

    //scriptComplete answers the javascript editor with the members the runtime exposes at the cursor
    private static async scriptComplete(message: PageMessage): Promise<void> {
        const result = await NcmActions.scriptComplete(
            message.source ?? "",
            message.line ?? 1,
            message.column ?? 1
        );

        void SettingsPanel.current?.webview.postMessage({
            type: "scriptComplete",
            id: message.id,
            ...result,
        });
    }

    //scriptCheck answers the javascript editor with the first syntax error, if there is one
    private static async scriptCheck(message: PageMessage): Promise<void> {
        const result = await NcmActions.scriptCheck(message.source ?? "");
        void SettingsPanel.current?.webview.postMessage({
            type: "scriptCheck",
            id: message.id,
            ...result,
        });
    }

    //taskFinished forwards the end of a task to the page, which clears its running row
    public static taskFinished(params: {
        action: string;
        ok: boolean;
        code: number;
        output: string;
    }): void {
        if (!params.action.startsWith("task:")) {
            return;
        }

        void SettingsPanel.current?.webview.postMessage({
            type: "taskFinished",
            name: params.action.slice("task:".length),
            ok: params.ok,
            output: params.output,
        });
    }

    //pushAsmDefault points the assembly page at a kernel assembly, which is the one worth opening first
    private static pushAsmDefault(): void {
        const project = ProjectLocator.find();
        if (!project) {
            return;
        }

        const kernel = path.join(project.root, "Build", "kernel");
        const candidate = ["NacreCraft.dll", "NacreCraft.Game.dll", "NacreCraft.Primitives.dll"]
            .map((name) => path.join(kernel, name))
            .find((file) => fs.existsSync(file));

        if (candidate) {
            void SettingsPanel.current?.webview.postMessage({
                type: "asmDefault",
                file: candidate,
            });
        }
    }

    //asm forwards one inspection to the server, which answers with structured data or rendered text
    private static async asm(message: PageMessage): Promise<void> {
        if (!message.file || !message.mode) {
            return;
        }

        const result = await NcmActions.asm({
            file: message.file,
            mode: message.mode,
            query: message.query,
            items: message.items,
        });

        void SettingsPanel.current?.webview.postMessage({
            type: "asmResult",
            id: message.id,
            ...result,
        });
    }

    //browseAssembly lets the page point at any assembly, starting from the project's kernel folder
    private static async browseAssembly(): Promise<void> {
        const project = ProjectLocator.find();
        const kernel = project ? path.join(project.root, "Build", "kernel") : null;
        const picked = await vscode.window.showOpenDialog({
            canSelectMany: false,
            openLabel: I18n.t("asm.browse"),
            defaultUri: kernel && fs.existsSync(kernel) ? vscode.Uri.file(kernel) : undefined,
            filters: { assembly: ["dll", "exe"] },
        });

        if (picked && picked.length > 0) {
            void SettingsPanel.current?.webview.postMessage({
                type: "asmFile",
                file: picked[0].fsPath,
            });
        }
    }

    //extractResource asks where to write one resource, then saves the bytes the server read
    private static async extractResource(file: string, name: string): Promise<void> {
        const result = await NcmActions.asm({ file, mode: "extract", query: name });
        if (!result.ok || !result.data) {
            void vscode.window.showErrorMessage(result.error ?? I18n.t("asm.failed"));
            return;
        }

        const target = await vscode.window.showSaveDialog({
            saveLabel: I18n.t("asm.extract"),
            defaultUri: vscode.Uri.file(path.join(path.dirname(file), result.name ?? "resource.bin")),
        });

        if (!target) {
            return;
        }

        await fs.promises.writeFile(target.fsPath, Buffer.from(result.data, "base64"));
        void vscode.window.showInformationMessage(
            `${I18n.t("asm.extracted")} ${target.fsPath}`
        );
    }

    //embedResources picks files and writes them into the assembly, replacing a resource of the same name
    private static async embedResources(file: string): Promise<void> {
        const picked = await vscode.window.showOpenDialog({
            canSelectMany: true,
            openLabel: I18n.t("asm.addRes"),
        });

        if (!picked || picked.length === 0) {
            return;
        }

        const result = await NcmActions.asm({
            file,
            mode: "embed",
            items: picked.map((item) => item.fsPath),
        });

        void SettingsPanel.current?.webview.postMessage({
            type: "asmEdited",
            ok: result.ok,
            text: result.ok ? result.text : result.error,
        });
    }

    //removeResource confirms first, since the rewrite cannot be undone from the page
    private static async removeResource(file: string, name: string): Promise<void> {
        const label = I18n.t("asm.remove");
        const choice = await vscode.window.showWarningMessage(
            `${I18n.t("asm.removeConfirm")}\n\n${name}`,
            { modal: true },
            label
        );

        if (choice !== label) {
            return;
        }

        const result = await NcmActions.asm({ file, mode: "remove", items: [name] });
        void SettingsPanel.current?.webview.postMessage({
            type: "asmEdited",
            ok: result.ok,
            text: result.ok ? result.text : result.error,
        });
    }

    //nugetReferences sends the kernel assemblies, the mod api switch and the declared packages
    private static async nugetReferences(id?: number): Promise<void> {
        const result = await NcmActions.references();
        void SettingsPanel.current?.webview.postMessage({
            type: "nugetRefs",
            id,
            ...result,
        });
    }

    //nugetSet changes one reference and answers with the references the change produced
    private static async nugetSet(message: PageMessage): Promise<void> {
        const result = await NcmActions.referenceSet({
            kind: message.kind === "modApi" ? "modApi" : "package",
            remove: message.remove === true,
            id: message.packageId,
            version: message.version,
        });

        void SettingsPanel.current?.webview.postMessage({
            type: "nugetSet",
            id: message.id,
            ...result,
        });
    }

    //nugetSearch answers from the cache the extension keeps, or asks the feed once more on a refresh
    private static async nugetSearch(message: PageMessage): Promise<void> {
        const result = await NuGetCache.search(
            message.query ?? "",
            message.source ?? "",
            message.refresh === true
        );

        void SettingsPanel.current?.webview.postMessage({
            type: "nugetSearch",
            id: message.id,
            ...result,
        });
    }

    //nugetReadme reads the document the package carries, which the description pane renders as markdown
    private static async nugetReadme(message: PageMessage): Promise<void> {
        const result = await NcmActions.nugetReadme(
            message.packageId ?? "",
            message.version ?? "",
            message.source ?? ""
        );

        void SettingsPanel.current?.webview.postMessage({
            type: "nugetReadme",
            id: message.id,
            ...result,
        });
    }

    //nugetVersions lists every published version, which the description pane offers to install
    private static async nugetVersions(message: PageMessage): Promise<void> {
        const result = await NcmActions.nugetVersions(
            message.packageId ?? "",
            message.source ?? ""
        );

        void SettingsPanel.current?.webview.postMessage({
            type: "nugetVersions",
            id: message.id,
            ...result,
        });
    }

    //focusNuGet closes both side bars so the package page gets the full width, unless the setting says otherwise
    private static focusNuGet(): void {
        if (Configuration.get("nuget.focus") !== "on") {
            return;
        }

        //a command this editor version does not know is ignored, the page works without it
        for (const command of [
            "workbench.action.closeSidebar",
            "workbench.action.closeAuxiliaryBar",
        ]) {
            vscode.commands.executeCommand(command).then(undefined, () => undefined);
        }
    }

    //openCreateIfPending opens the dialog once, on the first page that reports ready
    private static openCreateIfPending(): void {
        if (!SettingsPanel.pendingCreate) {
            return;
        }

        SettingsPanel.pendingCreate = false;
        SettingsPanel.current?.webview.postMessage({ type: "openCreate" });
    }

    //create runs ncm init, then offers to open the folder it produced
    private static async create(values: CreateValues): Promise<void> {
        Log.setStatus(I18n.t("status.creating"));
        Log.info(`creating ${values.name}`);
        SettingsPanel.current?.webview.postMessage({ type: "creating" });

        const target = await ProjectCreator.create(values);
        Log.setStatus("");

        if (target !== null) {
            Log.info(`created ${target}`);
        } else {
            Log.error("creation failed");
            void vscode.window.showErrorMessage(I18n.t("create.failed"), {
                modal: true,
            });
        }

        SettingsPanel.current?.webview.postMessage({
            type: "created",
            ok: target !== null,
            path: target ?? "",
        });

        if (target === null) {
            return;
        }

        const choice = await vscode.window.showInformationMessage(
            `NacreCraft: created ${path.basename(target)}. Open it now?`,
            "Open"
        );
        if (choice === "Open") {
            await vscode.commands.executeCommand(
                "vscode.openFolder",
                vscode.Uri.file(target)
            );
        }
    }

    //addReference picks the file the project should reference and lets ncm write it into the project
    private static async addReference(
        kind: "project" | "assembly"
    ): Promise<void> {
        const root = ProjectLocator.find()?.root;
        if (!root) {
            return;
        }

        const picked = await vscode.window.showOpenDialog({
            canSelectFiles: true,
            canSelectFolders: false,
            canSelectMany: false,
            defaultUri: vscode.Uri.file(root),
            openLabel: I18n.t(
                kind === "project" ? "refs.project.pick" : "refs.assembly.pick"
            ),
            filters:
                kind === "project"
                    ? { "C# project": ["csproj"] }
                    : { Assembly: ["dll"] },
        });

        if (!picked || picked.length === 0) {
            return;
        }

        const file = picked[0].fsPath;
        const args =
            kind === "project"
                ? ["add", "project", file]
                : ["add", "file", file];

        Log.setStatus(I18n.t("refs.adding"));
        const result = await Ncm.run(args, I18n.t("refs.adding"));
        Log.setStatus("");

        if (!result.ok) {
            const tail = stripAnsi(result.output)
                .trim()
                .split(/\r?\n/)
                .slice(-8)
                .join("\n");
            void vscode.window.showErrorMessage(
                tail.length > 0
                    ? `${I18n.t("action.failed")}\n\n${tail}`
                    : I18n.t("action.failed"),
                { modal: true }
            );
            return;
        }

        void vscode.window.showInformationMessage(I18n.t("refs.added"));
    }

    //openDiagnostic jumps to the place the language server pointed at
    private static async openDiagnostic(
        file: string,
        line: number,
        column: number
    ): Promise<void> {
        const document = await vscode.workspace.openTextDocument(
            vscode.Uri.file(file)
        );
        const position = new vscode.Position(
            Math.max(line - 1, 0),
            Math.max(column - 1, 0)
        );
        await vscode.window.showTextDocument(document, {
            selection: new vscode.Range(position, position),
        });
    }

    //NotifyProject sends the project and its rules again, which a manifest or project file change needs
    public static notifyProject(): void {
        SettingsPanel.pushProject();
    }

    //NotifyDiagnostics sends the problems the language server currently reports
    public static notifyDiagnostics(): void {
        SettingsPanel.current?.webview.postMessage({
            type: "diagnostics",
            items: SettingsPanel.diagnostics(),
        });
    }

    //push sends everything the page renders from the extension side
    private static push(): void {
        SettingsPanel.current?.webview.postMessage({
            type: "settings",
            items: SettingsPanel.items(),
        });
        SettingsPanel.pushProject();
        SettingsPanel.notifyDiagnostics();
        SettingsPanel.pushLog();
    }

    //pushProject sends the open mod and the icon uri the page can load
    private static pushProject(): void {
        const panel = SettingsPanel.current;
        if (!panel) {
            return;
        }

        const project = ProjectLocator.find();
        const icon = project?.icon
            ? panel.webview
                  .asWebviewUri(vscode.Uri.file(project.icon))
                  .toString()
            : null;

        panel.webview.postMessage({ type: "project", project, iconUri: icon });
    }

    //pushLog sends the status line and the log tail
    private static pushLog(): void {
        const { status, entries } = Log.snapshot();
        SettingsPanel.current?.webview.postMessage({
            type: "log",
            status,
            entries,
        });
    }

    //items reads the registered settings, translating the text the page shows
    private static items(): unknown[] {
        return Configuration.all().map((definition) => ({
            key: definition.key,
            label: I18n.t(definition.label),
            description: I18n.t(definition.description),
            options: definition.options ?? [],
            value: definition.value,
        }));
    }

    //whoever reports through the language server, which is the only source of problems in this extension
    private static diagnostics(): DiagnosticRow[] {
        const root = ProjectLocator.find()?.root ?? "";
        const rows: DiagnosticRow[] = [];

        for (const [uri, list] of vscode.languages.getDiagnostics()) {
            if (uri.scheme !== "file") {
                continue;
            }

            for (const diagnostic of list) {
                if (diagnostic.source !== "nacrecraft") {
                    continue;
                }

                const file = uri.fsPath;
                const relative =
                    root.length > 0 && file.startsWith(root)
                        ? path.relative(root, file).replace(/\\/g, "/")
                        : file;

                rows.push({
                    file,
                    display: relative,
                    line: diagnostic.range.start.line + 1,
                    column: diagnostic.range.start.character + 1,
                    severity:
                        diagnostic.severity === vscode.DiagnosticSeverity.Error
                            ? "error"
                            : "warning",
                    message: diagnostic.message,
                });
            }
        }

        rows.sort(
            (a, b) =>
                (a.severity === b.severity
                    ? 0
                    : a.severity === "error"
                      ? -1
                      : 1) ||
                a.display.localeCompare(b.display) ||
                a.line - b.line ||
                a.column - b.column
        );

        return rows;
    }

    //xmlGrammar returns the TextMate grammar VS Code ships for XML, or null when it cannot be read
    //Using the extension's own file is what puts the page on exactly the scopes the editor uses
    private static xmlGrammar(): string {
        const extension = vscode.extensions.all.find((item) =>
            (item.packageJSON?.contributes?.languages ?? []).some(
                (language: { id?: string }) => language.id === "xml"
            )
        );

        if (!extension) {
            return "null";
        }

        try {
            const text = fs.readFileSync(
                path.join(extension.extensionPath, "syntaxes", "xml.tmLanguage.json"),
                "utf8"
            );
            return text.length > 0 ? text : "null";
        } catch {
            return "null";
        }
    }

    //html assembles the frame: the styles and the page scripts are separate files under media
    private static html(
        webview: vscode.Webview,
        context: vscode.ExtensionContext
    ): string {
        const nonce = randomBytes(16).toString("base64");
        const asset = (name: string) =>
            webview
                .asWebviewUri(vscode.Uri.joinPath(context.extensionUri, "media", name))
                .toString();

        //assemblies and packages without an icon of their own show the extension mark
        const icon = asset("nacrecraft.svg");

        //the page tokenizes the task xml with the grammar the editor itself uses, so the colors follow its scopes
        const grammar = SettingsPanel.xmlGrammar();

        return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}' ${webview.cspSource} 'wasm-unsafe-eval'; connect-src ${webview.cspSource}; img-src ${webview.cspSource} data: https:;">
<link rel="stylesheet" href="${asset("page.css")}">
<link rel="stylesheet" href="${asset("codemirror.min.css")}">
</head>
<body>${panelMarkup}
<script nonce="${nonce}">
    window.__nacrecraftText = ${JSON.stringify(I18n.dump())};
    window.__nacrecraftIcon = ${JSON.stringify(icon)};
    window.__nacrecraftXmlGrammar = ${grammar};
    window.__nacrecraftOnigWasm = ${JSON.stringify(asset("onig.wasm"))};
</script>
<script nonce="${nonce}" src="${asset("textmate.js")}"></script>
<script nonce="${nonce}" src="${asset("codemirror.min.js")}"></script>
<script nonce="${nonce}" src="${asset("ncjs-mode.js")}"></script>
<script nonce="${nonce}" src="${asset("codemirror-simple-hint.js")}"></script>
<script nonce="${nonce}" src="${asset("codemirror-xml.min.js")}"></script>
<script nonce="${nonce}" src="${asset("codemirror-xml-fold.min.js")}"></script>
<script nonce="${nonce}" src="${asset("codemirror-closetag.min.js")}"></script>
<script nonce="${nonce}" src="${asset("codemirror-matchbrackets.min.js")}"></script>
<script nonce="${nonce}" src="${asset("codemirror-matchtags.min.js")}"></script>
<script nonce="${nonce}" src="${asset("codemirror-activeline.min.js")}"></script>
<script nonce="${nonce}" src="${asset("gui.js")}"></script>
<script nonce="${nonce}" src="${asset("base.js")}"></script>
<script nonce="${nonce}" src="${asset("marked.min.js")}"></script>
<script nonce="${nonce}" src="${asset("asm.js")}"></script>
<script nonce="${nonce}" src="${asset("nuget.js")}"></script>
<script nonce="${nonce}" src="${asset("config.js")}"></script>
<script nonce="${nonce}" src="${asset("tasks.js")}"></script>
<script nonce="${nonce}" src="${asset("page.js")}"></script>
</body>
</html>`;
    }
}

//PageMessage the messages the page sends to the extension
interface PageMessage {
    type:
        | "ready"
        | "update"
        | "openJson"
        | "create"
        | "addRef"
        | "openDiagnostic"
        | "asm"
        | "asmBrowse"
        | "asmExtract"
        | "asmEmbed"
        | "asmRemove"
        | "nugetRefs"
        | "nugetSet"
        | "nugetSearch"
        | "nugetReadme"
        | "nugetVersions"
        | "nugetFocus"
        | "config"
        | "configSet"
        | "tasks"
        | "tasksSaveTask"
        | "tasksRemove"
        | "tasksOverride"
        | "taskRun"
        | "scriptComplete"
        | "scriptCheck";
    key?: string;
    value?: string;
    values?: CreateValues;
    //the task page names the edited block and the task to run
    xml?: string;
    enabled?: boolean;
    name?: string;
    kind?: "project" | "assembly" | "package" | "modApi";
    file?: string;
    line?: number;
    column?: number;
    id?: number;
    mode?: AsmRequest["mode"];
    query?: string;
    items?: string[];
    //the ncproj that declares an aggregated bootstrap task
    config?: string;
    //one package install or removal the package page asked for
    packageId?: string;
    version?: string;
    remove?: boolean;
    source?: string;
    //asks the feed again instead of answering from the cache
    refresh?: boolean;
}
