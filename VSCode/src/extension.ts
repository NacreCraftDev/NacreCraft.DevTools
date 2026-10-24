import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    TransportKind,
} from "vscode-languageclient/node";
import { Configuration } from "./config";
import { FinishedParams, NcmActions } from "./actions";
import { I18n } from "./i18n";
import { Log } from "./log";
import { NuGetCache } from "./nugetCache";
import { PageView } from "./pageView";
import { ProjectTree } from "./projectTree";
import { SettingsPanel } from "./settingsPanel";
import { TaskTree } from "./taskTree";

let client: LanguageClient | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    //settings are declared first, the language and the page both read from them
    registerSettings(context);
    Configuration.initialize(context);

    //the language has to be resolved before anything renders text
    await I18n.initialize(context);

    //the file icons are opt in, since a theme replaces every icon the user has today
    offerIconTheme(context);

    const server = serverCommand(context);
    if (!server) {
        vscode.window.showErrorMessage(
            "NacreCraft: the language server is missing from the extension."
        );
        return;
    }

    const serverOptions: ServerOptions = {
        run: { command: server.command, args: server.args, transport: TransportKind.stdio },
        debug: { command: server.command, args: server.args, transport: TransportKind.stdio },
    };

    const clientOptions: LanguageClientOptions = {
        documentSelector: [
            { scheme: "file", language: "json", pattern: "**/ncmod.json" },
            //the whole project is checked for a source save, the editor only has to hand the document over
            { scheme: "file", language: "csharp", pattern: "**/*.cs" },
            //task scripts complete from the tracked buffer, so an unsaved script still sees the ncm api
            { scheme: "file", language: "javascript", pattern: "**/*.{js,mjs,cjs}" },
        ],
        //the server fetches the kernel only when this is on, which is what keeps opening a project off the network
        initializationOptions: {
            autoUpdateKernel: Configuration.get("kernel.autoUpdate") === "on",
            autoCheck: Configuration.get("check.auto") === "on",
            completionBuild: Configuration.get("completion.build") === "on",
            projectLoad: Configuration.get("project.load"),
        },
    };

    client = new LanguageClient(
        "nacrecraft-ncmod",
        "NacreCraft NcMod",
        serverOptions,
        clientOptions
    );

    //the server reports its own progress through the standard log notification
    client.onNotification(
        "window/logMessage",
        (params: { type: number; message: string }) => {
            if (params.type === 1) {
                Log.error(params.message);
            } else if (params.type === 2) {
                Log.warn(params.message);
            } else {
                Log.info(params.message);
            }
        }
    );

    //the server cannot see writes made outside the editor, so the client watches the sources and says when they move
    //the switch is read on each event, which makes turning it off take effect without restarting anything
    const watcher = vscode.workspace.createFileSystemWatcher(
        "**/{*.cs,*.csproj,*.ncproj,ncmod.json}"
    );
    let pendingCheck: NodeJS.Timeout | undefined;
    const changed = (uri: vscode.Uri): void => {
        //the task list follows the config file even when source watching is off
        if (uri.fsPath.endsWith(".ncproj")) {
            taskTree.refresh();
        }

        if (Configuration.get("check.watch") !== "on") {
            return;
        }

        //build output churns hundreds of files and is not something to check
        if (/(^|[\\/])(obj|bin|Build)([\\/]|$)/.test(uri.fsPath)) {
            return;
        }

        if (pendingCheck) {
            clearTimeout(pendingCheck);
        }

        pendingCheck = setTimeout(() => {
            pendingCheck = undefined;
            void client?.sendNotification("workspace/didChangeWatchedFiles", {
                changes: [{ uri: uri.toString(), type: 2 }],
            });
        }, 500);
    };

    context.subscriptions.push(
        watcher,
        watcher.onDidChange(changed),
        watcher.onDidCreate(changed),
        watcher.onDidDelete(changed)
    );

    //the side bar buttons drive the server, which owns the ncm processes and the interrupt that stops them
    NcmActions.attach(client);
    client.onNotification(
        "nacrecraft/progress",
        (params: { action: string; message: string }) =>
            NcmActions.progress(params.action, params.message)
    );
    client.onNotification(
        "nacrecraft/finished",
        (params: FinishedParams) => {
            NcmActions.finished(params);
            SettingsPanel.taskFinished(params);
        }
    );
    client.onNotification(
        "nacrecraft/state",
        (params: { running: string[] }) => NcmActions.state(params.running)
    );

    Log.setStatus(I18n.t("status.startingServer"));
    Log.info("starting the language server");
    client.start().then(
        () => {
            Log.info("language server started");
            Log.setStatus("");

            //the package tab opens on a listing that is already cached, so the feed is asked once here
            void NuGetCache.preload(firstNuGetSource());

            //the task list can only be read once the server answers
            taskTree.refresh();
        },
        (error) => {
            const message = String(error);

            //the editor closed the window before the handshake finished, which is a shutdown and not a failure
            if (
                message.includes("connection got disposed") ||
                message.includes("Pending response rejected")
            ) {
                Log.info("session closed before the language server was ready");
                Log.setStatus("");
                return;
            }

            Log.error(`language server failed: ${message}`);
            Log.setStatus("");
            vscode.window.showErrorMessage(
                `NacreCraft: the language server failed to start: ${error}`
            );
        }
    );

    const pageView = new PageView(context);
    context.subscriptions.push(
        vscode.window.registerWebviewViewProvider(
            PageView.viewType,
            pageView
        )
    );

    //the side bar shows the mod of this workspace, empty when the folder is not a mod
    const projectTree = new ProjectTree();
    context.subscriptions.push(
        vscode.window.createTreeView("nacrecraft.project", {
            treeDataProvider: projectTree,
        })
    );

    //the project's own tasks sit beside it, and a click runs one
    const taskTree = new TaskTree();
    context.subscriptions.push(
        vscode.window.createTreeView("nacrecraft.tasks", {
            treeDataProvider: taskTree,
        })
    );
    taskTree.refresh();

    context.subscriptions.push(
        vscode.commands.registerCommand("nacrecraft.runTask", (name: string) =>
            SettingsPanel.runTask(name)
        )
    );
    context.subscriptions.push(
        vscode.workspace.onDidChangeWorkspaceFolders(() => {
            projectTree.refresh();
            taskTree.refresh();
        })
    );
    context.subscriptions.push(
        vscode.workspace.onDidSaveTextDocument((document) => {
            if (
                document.fileName.endsWith("ncmod.json") ||
                document.fileName.endsWith(".ncproj")
            ) {
                projectTree.refresh();
                taskTree.refresh();
                SettingsPanel.notifyProject();
            }
        })
    );

    //the page lists what the language server reported, so a new push redraws it
    context.subscriptions.push(
        vscode.languages.onDidChangeDiagnostics(() =>
            SettingsPanel.notifyDiagnostics()
        )
    );

    //a language change takes effect without reloading the window
    context.subscriptions.push(
        Configuration.changed(async (key) => {
            //a size dragged out on the page only needs the settings list to follow it
            if (key === "log.height" || key.startsWith("nuget.detail")) {
                SettingsPanel.refreshSettings();
                return;
            }

            Log.setStatus(I18n.t("status.reloading"));
            await I18n.reload(context);
            SettingsPanel.refresh();
            pageView.refresh();
            projectTree.refresh();

            //the server keeps the setting in memory, so it is told instead of being restarted
            void client?.sendNotification("workspace/didChangeConfiguration", {
                settings: {
                    autoUpdateKernel:
                        Configuration.get("kernel.autoUpdate") === "on",
                    autoCheck: Configuration.get("check.auto") === "on",
                    completionBuild:
                        Configuration.get("completion.build") === "on",
                    projectLoad: Configuration.get("project.load"),
                },
            });

            Log.info("configuration reloaded");
            Log.setStatus("");
        })
    );

    context.subscriptions.push(
        vscode.commands.registerCommand("nacrecraft.openPage", () =>
            SettingsPanel.show(context)
        )
    );

    //the create entry in the side bar lands on the page with the dialog already open
    context.subscriptions.push(
        vscode.commands.registerCommand("nacrecraft.create", () =>
            SettingsPanel.show(context, true)
        )
    );

    context.subscriptions.push(
        vscode.commands.registerCommand("nacrecraft.checkManifest", async () => {
            const editor = vscode.window.activeTextEditor;
            if (editor && editor.document.fileName.endsWith("ncmod.json")) {
                await editor.document.save();
                vscode.window.showInformationMessage("NacreCraft: ncmod.json checked.");
                return;
            }

            vscode.window.showInformationMessage(
                "NacreCraft: open an ncmod.json first."
            );
        })
    );
}

export function deactivate(): Thenable<void> | undefined {
    return client?.stop();
}

//offerIconTheme points the workbench at the bundled seti based theme once
//The command stays registered, so the switch can be made again after declining
function offerIconTheme(context: vscode.ExtensionContext): void {
    context.subscriptions.push(
        vscode.commands.registerCommand("nacrecraft.enableIconTheme", () =>
            vscode.workspace
                .getConfiguration("workbench")
                .update("iconTheme", "nacrecraft", vscode.ConfigurationTarget.Global)
        )
    );

    const workbench = vscode.workspace.getConfiguration("workbench");
    if (workbench.get<string>("iconTheme") === "nacrecraft") {
        return;
    }

    if (context.globalState.get<boolean>("nacrecraft.iconThemeOffered")) {
        return;
    }

    void context.globalState.update("nacrecraft.iconThemeOffered", true);
    void vscode.window
        .showInformationMessage(
            I18n.t("iconTheme.offer"),
            I18n.t("iconTheme.enable"),
            I18n.t("iconTheme.later")
        )
        .then((choice) => {
            if (choice === I18n.t("iconTheme.enable")) {
                void workbench.update(
                    "iconTheme",
                    "nacrecraft",
                    vscode.ConfigurationTarget.Global
                );
            }
        });
}

//serverCommand resolves how to start the server
//The published server ships an apphost next to the dll, which runs without a dotnet on PATH; a configured path may be
//an executable, a dll or the folder holding either, and the bundled folder is the fallback
function serverCommand(
    context: vscode.ExtensionContext
): { command: string; args: string[] } | undefined {
    const configured = Configuration.get("langServer.path");
    if (configured.length > 0) {
        const resolved =
            fs.existsSync(configured) && fs.statSync(configured).isDirectory()
                ? serverIn(configured)
                : serverIn(path.dirname(configured)) ??
                  configuredEntry(configured);

        if (resolved) {
            return resolved;
        }

        //the configured path leads nowhere, fall through to the bundled server
        Log.warn(`language server path ${configured} was not usable, using the bundled one`);
    }

    return serverIn(context.asAbsolutePath(path.join("server")));
}

//serverIn finds the launcher inside a folder
function serverIn(
    directory: string
): { command: string; args: string[] } | undefined {
    //the published server keeps one small apphost per platform, all loading the same dll
    const rid = serverRid();
    if (rid) {
        const apphost = path.join(
            directory,
            "apphost",
            rid,
            process.platform === "win32"
                ? "NacreCraft.NcMod.LangServer.exe"
                : "NacreCraft.NcMod.LangServer"
        );

        if (fs.existsSync(apphost)) {
            //an archive unpacked on unix may lose the executable bit
            if (process.platform !== "win32") {
                try {
                    fs.chmodSync(apphost, 0o755);
                } catch {
                    //a read only install keeps whatever mode it already has
                }
            }

            return { command: apphost, args: [] };
        }
    }

    const executable = path.join(
        directory,
        process.platform === "win32"
            ? "NacreCraft.NcMod.LangServer.exe"
            : "NacreCraft.NcMod.LangServer"
    );
    if (fs.existsSync(executable)) {
        return { command: executable, args: [] };
    }

    const dll = path.join(directory, "NacreCraft.NcMod.LangServer.dll");
    return fs.existsSync(dll)
        ? { command: dotnetCommand(), args: [dll] }
        : undefined;
}

//serverRid names this process the way the published apphost folders do
//An architecture without a published apphost falls back to the dll, which the runtime can still start
function serverRid(): string | undefined {
    const platform =
        process.platform === "win32"
            ? "win"
            : process.platform === "darwin"
              ? "osx"
              : process.platform === "linux"
                ? "linux"
                : undefined;
    if (!platform) {
        return undefined;
    }

    const architecture =
        process.arch === "arm64"
            ? "arm64"
            : process.arch === "x64"
              ? "x64"
              : undefined;
    if (!architecture) {
        return undefined;
    }

    //only these four apphosts are published
    if (platform !== "osx" && architecture !== "x64") {
        return undefined;
    }

    return `${platform}-${architecture}`;
}

//configuredEntry interprets a configured path that is a file rather than a folder
function configuredEntry(
    file: string
): { command: string; args: string[] } | undefined {
    if (!fs.existsSync(file)) {
        return undefined;
    }

    return file.toLowerCase().endsWith(".dll")
        ? { command: dotnetCommand(), args: [file] }
        : { command: file, args: [] };
}

//dotnetCommand finds the runtime, since a window started from the desktop may not carry it on PATH
function dotnetCommand(): string {
    const root = process.env.DOTNET_ROOT;
    if (root) {
        const candidate = path.join(
            root,
            process.platform === "win32" ? "dotnet.exe" : "dotnet"
        );
        if (fs.existsSync(candidate)) {
            return candidate;
        }
    }

    return "dotnet";
}

//firstNuGetSource the feed the package page opens on, which is the first one the settings name
function firstNuGetSource(): string {
    const sources = Configuration.get("nuget.sources")
        .split(/[;,\r\n]+/)
        .map((source) => source.trim())
        .filter((source) => source.length > 0);

    return sources[0] ?? "https://api.nuget.org/v3/index.json";
}

//registerSettings declares everything the page can edit
//A new setting only needs an entry here and its two translation keys
function registerSettings(context: vscode.ExtensionContext): void {
    Configuration.register({
        key: "kernel.autoUpdate",
        defaultValue: "off",
        options: ["on", "off"],
        label: "settings.kernelAutoUpdate.label",
        description: "settings.kernelAutoUpdate.description",
    });

    Configuration.register({
        key: "check.auto",
        defaultValue: "on",
        options: ["on", "off"],
        label: "settings.autoCheck.label",
        description: "settings.autoCheck.description",
    });

    Configuration.register({
        key: "check.watch",
        defaultValue: "on",
        options: ["on", "off"],
        label: "settings.watch.label",
        description: "settings.watch.description",
    });

    //a completion request must never start a silent full build on its own
    Configuration.register({
        key: "completion.build",
        defaultValue: "off",
        options: ["on", "off"],
        label: "settings.completionBuild.label",
        description: "settings.completionBuild.description",
    });

    //one workspace may hold several unrelated mods, so loading all of them is a choice
    Configuration.register({
        key: "project.load",
        defaultValue: "onDemand",
        options: ["onDemand", "all"],
        label: "settings.projectLoad.label",
        description: "settings.projectLoad.description",
    });

    Configuration.register({
        key: "nuget.sources",
        defaultValue: "https://api.nuget.org/v3/index.json",
        label: "settings.nugetSources.label",
        description: "settings.nugetSources.description",
    });

    Configuration.register({
        key: "nuget.focus",
        defaultValue: "on",
        options: ["on", "off"],
        label: "settings.nugetFocus.label",
        description: "settings.nugetFocus.description",
    });

    Configuration.register({
        key: "nuget.detailWidth",
        defaultValue: "440",
        label: "settings.nugetDetailWidth.label",
        description: "settings.nugetDetailWidth.description",
    });

    Configuration.register({
        key: "nuget.detailHeight",
        defaultValue: "560",
        label: "settings.nugetDetailHeight.label",
        description: "settings.nugetDetailHeight.description",
    });

    Configuration.register({
        key: "log.height",
        defaultValue: "120",
        label: "settings.logHeight.label",
        description: "settings.logHeight.description",
    });

    Configuration.register({
        key: "language",
        defaultValue: "",
        options: ["", ...I18n.languages(context)],
        label: "settings.language.label",
        description: "settings.language.description",
    });

    Configuration.register({
        key: "langServer.path",
        defaultValue: "",
        label: "settings.langServerPath.label",
        description: "settings.langServerPath.description",
    });
}
