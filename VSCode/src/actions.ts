import * as vscode from "vscode";
import { State, type LanguageClient } from "vscode-languageclient/node";
import { stripAnsi } from "./ansi";
import { I18n } from "./i18n";
import { Log } from "./log";
import { ProjectLocator } from "./project";

//SidebarAction what the side bar can start from a mod workspace
export type SidebarAction = "build" | "restore" | "runclient" | "runserver" | "clean";

//LaunchAction the two actions that keep a process running, so the side bar can offer to stop them
export type LaunchAction = "runclient" | "runserver";

//FinishedParams what the language server reports about one command
export interface FinishedParams {
    action: SidebarAction;
    ok: boolean;
    code: number;
    output: string;
}

//AsmRequest one inspection the assembly page asks the server to run
export interface AsmRequest {
    file: string;
    mode:
        | "summary"
        | "type"
        | "decompile"
        | "il"
        | "dependencies"
        | "resources"
        | "extract"
        | "embed"
        | "remove";
    query?: string;
    items?: string[];
}

//AsmResult what the server answers; text carries rendered output, the rest is structured
export interface AsmResult {
    ok: boolean;
    error?: string;
    text?: string;
    name?: string;
    version?: string;
    methods?: number;
    types?: { kind: string; name: string }[];
    resources?: { name: string; size: number; kind: string }[];
    data?: string;
}

//ReferenceInfo one NuGet package the project declares
export interface ReferenceInfo {
    id: string;
    version: string;
}

//ReferencesResult what the project references, read from the project file
export interface ReferencesResult {
    ok: boolean;
    error?: string;
    config?: string;
    //file names of the kernel assemblies, which the page lists under the kernel entry
    kernel?: string[];
    modApi?: boolean;
    packages?: ReferenceInfo[];
}

//ReferenceChange one install or removal the package page asked for
export interface ReferenceChange {
    kind: "package" | "modApi";
    remove: boolean;
    id?: string;
    version?: string;
}

//PackageInfo one search hit from a feed
export interface PackageInfo {
    id: string;
    version: string;
    description: string;
    icon: string;
    downloads: number;
}

//PackageSearchResult the answer to one feed search
export interface PackageSearchResult {
    ok: boolean;
    error?: string;
    packages?: PackageInfo[];
}

//ReadmeResult the markdown document a package carries
export interface ReadmeResult {
    ok: boolean;
    error?: string;
    text?: string;
}

//VersionsResult every version a package published, newest first
export interface VersionsResult {
    ok: boolean;
    error?: string;
    versions?: string[];
}

//ConfigEntry one setting the project config may declare
//present is false for one the file leaves out, which the page offers in a lighter color
export interface ConfigEntry {
    key: string;
    group: string;
    label: string;
    kind: "bool" | "enum" | "text";
    options: string[];
    default: string;
    value: string;
    present: boolean;
    required: boolean;
}

//ConfigResult the settings of a project, after one change
export interface ConfigResult {
    ok: boolean;
    error?: string;
    config?: string;
    entries?: ConfigEntry[];
}

//TaskEntry one task the project config declares
//shadowed means the name collides with a built-in tool and Override is off, so it cannot run
export interface TaskEntry {
    name: string;
    description: string;
    depends: string[];
    overrides: boolean;
    shadowed: boolean;
    steps: string[];
    //the task element as written, which the side pane edits on its own
    xml: string;
    //the ncproj holding the task, which a bootstrap task differs from the workspace root
    config: string;
}

//TasksResult the task list and the override default
export interface TasksResult {
    ok: boolean;
    error?: string;
    config?: string;
    overrides?: boolean;
    tasks?: TaskEntry[];
}

//TaskRunResult either a started task or one waiting for the page to confirm an override
export interface TaskRunResult {
    ok: boolean;
    error?: string;
    needsApproval?: boolean;
    name?: string;
    steps?: string[];
}

//ScriptCompletion one runtime member the javascript editor can insert
export interface ScriptCompletion {
    name: string;
    kind: string;
}

//ScriptCompletionResult the members at the cursor, built from the same environment a task run gets
export interface ScriptCompletionResult {
    ok: boolean;
    error?: string;
    items?: ScriptCompletion[];
}

//ScriptDiagnostic the first syntax error of a script
export interface ScriptDiagnostic {
    line: number;
    column: number;
    message: string;
}

//ScriptCheckResult the parse result of a script, diagnostic only being set when it does not parse
export interface ScriptCheckResult {
    ok: boolean;
    error?: string;
    diagnostic?: ScriptDiagnostic;
}

interface Pending {
    progress: vscode.Progress<{ message?: string }> | undefined;
    finish: (() => void) | undefined;
}

//NcmActions drives the commands through the language server
//The server owns the processes, which is what lets it stop a launch with a console interrupt; the editor only sends
//requests and shows the output and the result that come back
export class NcmActions {
    //the command line names and the translated labels differ for the two launches
    private static readonly labels: Record<SidebarAction, string> = {
        build: "action.build",
        restore: "action.restore",
        runclient: "action.runClient",
        runserver: "action.runServer",
        clean: "action.clean",
    };

    //the action that ends a launch is named after it, which the button and the status line both use
    private static readonly stopLabels: Record<LaunchAction, string> = {
        runclient: "action.stopClient",
        runserver: "action.stopServer",
    };

    private static client: LanguageClient | undefined;

    //launches the server reported running, which is what the buttons read
    private static readonly running = new Set<string>();

    //commands in flight, each with the notification that shows its output
    private static readonly pending = new Map<SidebarAction, Pending>();

    //Fires when a launch starts or ends, which is what flips the side bar button
    public static readonly changed = new vscode.EventEmitter<void>();

    //Attach gives the actions the connection they talk over
    public static attach(client: LanguageClient): void {
        NcmActions.client = client;
    }

    //Ready whether the handshake finished; a request sent before it never settles
    public static get ready(): boolean {
        return NcmActions.client?.state === State.Running;
    }

    //IsRunning whether the language server still holds a process for the action
    public static isRunning(action: LaunchAction): boolean {
        return NcmActions.running.has(action);
    }

    public static async run(action: SidebarAction): Promise<void> {
        const client = NcmActions.client;
        if (
            !client ||
            ProjectLocator.find() === null ||
            NcmActions.pending.has(action)
        ) {
            return;
        }

        const title = I18n.t(NcmActions.labels[action]);
        Log.setStatus(title);

        const entry: Pending = { progress: undefined, finish: undefined };
        NcmActions.pending.set(action, entry);

        //the notification opens before the request leaves, so no output line can arrive before it
        void vscode.window.withProgress(
            {
                location: vscode.ProgressLocation.Notification,
                title,
                cancellable: false,
            },
            (progress) =>
                new Promise<void>((resolve) => {
                    entry.progress = progress;
                    entry.finish = resolve;
                })
        );

        try {
            const accepted = await client.sendRequest<boolean>("nacrecraft/run", {
                action,
            });

            if (!accepted) {
                NcmActions.abandon(action);
            }
        } catch (error) {
            NcmActions.settle(action, {
                action,
                ok: false,
                code: -1,
                output: String(error),
            });
        }
    }

    //stop asks the server to interrupt the launch, which is a Ctrl+C on its side
    public static async stop(action: LaunchAction): Promise<void> {
        const client = NcmActions.client;
        if (!client) {
            return;
        }

        Log.setStatus(I18n.t(NcmActions.stopLabels[action]));
        try {
            await client.sendRequest<boolean>("nacrecraft/stop", { action });
        } catch (error) {
            Log.warn(`stopping ${action} failed: ${String(error)}`);
        }
    }

    //asm runs one inspection inside the server, which links the asm code directly instead of starting a process
    public static async asm(request: AsmRequest): Promise<AsmResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<AsmResult>("nacrecraft/asm", request);
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //references reads the kernel assemblies, the mod api switch and the declared packages
    public static async references(): Promise<ReferencesResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<ReferencesResult>(
                "nacrecraft/references",
                {}
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //referenceSet installs or removes one reference and answers with the list that results from it
    public static async referenceSet(
        change: ReferenceChange
    ): Promise<ReferencesResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<ReferencesResult>(
                "nacrecraft/referenceSet",
                change
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //config reads every setting the project config may declare, the file's own values included
    public static async config(): Promise<ConfigResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<ConfigResult>("nacrecraft/config", {});
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //configSet writes or removes one setting and answers with the list that results from it
    public static async configSet(
        key: string,
        value: string,
        remove: boolean
    ): Promise<ConfigResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<ConfigResult>(
                "nacrecraft/configSet",
                { key, value, remove }
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //tasks reads what the project declares, the override default and the task xml
    public static async tasks(): Promise<TasksResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<TasksResult>("nacrecraft/tasks", {});
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //tasksSaveTask replaces one task, or appends it when name is empty
    public static async tasksSaveTask(
        name: string,
        xml: string,
        config = ""
    ): Promise<TasksResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<TasksResult>(
                "nacrecraft/tasksSaveTask",
                { name, xml, config }
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //tasksRemove drops one declared task and answers with the list that remains
    public static async tasksRemove(
        name: string,
        config = ""
    ): Promise<TasksResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<TasksResult>(
                "nacrecraft/tasksRemove",
                { name, config }
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //tasksOverride writes the default that lets a task replace a built-in tool
    public static async tasksOverride(value: boolean): Promise<TasksResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<TasksResult>(
                "nacrecraft/tasksOverride",
                { value }
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //taskRun starts one task, approve confirming a task that replaces a built-in tool
    public static async taskRun(
        name: string,
        approve: boolean
    ): Promise<TaskRunResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<TaskRunResult>(
                "nacrecraft/taskRun",
                { name, approve }
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //scriptComplete lists the members the runtime exposes at the cursor
    public static async scriptComplete(
        source: string,
        line: number,
        column: number
    ): Promise<ScriptCompletionResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<ScriptCompletionResult>(
                "nacrecraft/scriptComplete",
                { source, line, column }
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //scriptCheck parses the script, which the page shows as a status line
    public static async scriptCheck(
        source: string
    ): Promise<ScriptCheckResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<ScriptCheckResult>(
                "nacrecraft/scriptCheck",
                { source }
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //nugetSearch asks one feed for packages, the source being its v3 index or a search endpoint
    public static async nugetSearch(
        query: string,
        source: string
    ): Promise<PackageSearchResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<PackageSearchResult>(
                "nacrecraft/nugetSearch",
                { query, source }
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //nugetReadme reads the markdown document a package carries, which the description pane renders
    public static async nugetReadme(
        id: string,
        version: string,
        source: string
    ): Promise<ReadmeResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<ReadmeResult>(
                "nacrecraft/nugetReadme",
                { id, version, source }
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //nugetVersions lists every published version of a package, used by the version picker
    public static async nugetVersions(
        id: string,
        source: string
    ): Promise<VersionsResult> {
        const client = NcmActions.client;
        if (!client) {
            return { ok: false, error: I18n.t("asm.noServer") };
        }

        try {
            return await client.sendRequest<VersionsResult>(
                "nacrecraft/nugetVersions",
                { id, source }
            );
        } catch (error) {
            return { ok: false, error: String(error) };
        }
    }

    //progress shows one line of output inside the notification of that command
    public static progress(action: string, message: string): void {
        const entry = NcmActions.pending.get(action as SidebarAction);
        entry?.progress?.report({ message: NcmActions.shorten(message) });
    }

    //finished closes the notification and reports the result
    //A project task reports to the task page instead, which shows the failure in place
    public static finished(params: FinishedParams): void {
        if (params.action.startsWith("task:")) {
            return;
        }

        NcmActions.settle(params.action, params);
    }

    //state replaces which launches are running, as reported by the server
    public static state(running: string[]): void {
        NcmActions.running.clear();
        for (const action of running) {
            NcmActions.running.add(action);
        }
        NcmActions.changed.fire();
    }

    //abandon closes a command the server refused to start
    private static abandon(action: SidebarAction): void {
        const entry = NcmActions.pending.get(action);
        NcmActions.pending.delete(action);
        entry?.finish?.();
        Log.setStatus("");
    }

    private static settle(action: SidebarAction, result: FinishedParams): void {
        const entry = NcmActions.pending.get(action);
        NcmActions.pending.delete(action);
        entry?.finish?.();
        Log.setStatus("");

        if (!result.ok) {
            NcmActions.fail(result.output);
            return;
        }

        //a clean is the one command with a number worth repeating
        if (action === "clean") {
            const freed = /Freed\s+([0-9.]+)\s*MB/i.exec(result.output);
            void vscode.window.showInformationMessage(
                freed
                    ? `${I18n.t("action.cleaned")} ${freed[1]} MB`
                    : I18n.t("action.cleaned")
            );
        }
    }

    //fail shows the tail of the output, which carries the error ncm ended on
    private static fail(output: string): void {
        const tail = stripAnsi(output)
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
    }

    //progress text has one line, so a very long message is cut instead of wrapping the notification
    private static shorten(line: string): string {
        const text = stripAnsi(line).replace(/\t/g, " ").trim();
        return text.length > 120 ? text.slice(0, 117) + "..." : text;
    }
}
