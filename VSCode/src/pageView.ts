import { randomBytes } from "crypto";
import * as vscode from "vscode";
import { SidebarAction, NcmActions } from "./actions";
import { I18n } from "./i18n";
import { ProjectLocator } from "./project";
import { SettingsPanel } from "./settingsPanel";

//PageView is the side bar entry
//On a mod it lists the actions a mod workspace needs, on any other folder it keeps the creation entry, and the text
//follows the language setting since it is rendered here rather than declared in package.json
export class PageView implements vscode.WebviewViewProvider {
    public static readonly viewType = "nacrecraft.page";

    private opened = false;

    //the live view, kept so a language or project change can redraw it without reopening the side bar
    private view: vscode.WebviewView | undefined;

    constructor(private readonly context: vscode.ExtensionContext) {
        //a launch starting or ending flips its button between start and stop
        context.subscriptions.push(
            NcmActions.changed.event(() => this.refresh())
        );
    }

    public resolveWebviewView(view: vscode.WebviewView): void {
        this.view = view;
        view.webview.options = { enableScripts: true };
        this.render();

        view.webview.onDidReceiveMessage((message) => {
            if (message.type !== "action" || typeof message.action !== "string") {
                return;
            }

            if (message.action === "open") {
                SettingsPanel.show(this.context);
                return;
            }

            if (message.action === "create") {
                SettingsPanel.show(this.context, true);
                return;
            }

            if (message.action === "stopclient") {
                void NcmActions.stop("runclient");
                return;
            }

            if (message.action === "stopserver") {
                void NcmActions.stop("runserver");
                return;
            }

            void NcmActions.run(message.action as SidebarAction);
        });

        //a mod project wants its page, a plain folder stays on the side bar and shows the create entry there
        if (!this.opened && ProjectLocator.find() !== null) {
            this.opened = true;
            SettingsPanel.show(this.context);
        }
    }

    //Refresh redraws the view, which follows both the language setting and the project state
    public refresh(): void {
        this.render();
    }

    private render(): void {
        if (this.view) {
            this.view.webview.html = this.html(this.view.webview);
        }
    }

    private html(webview: vscode.Webview): string {
        const nonce = randomBytes(16).toString("base64");
        const isMod = ProjectLocator.find() !== null;

        const actions: Array<[string, string]> = isMod
            ? [
                  ["open", "view.open"],
                  ["build", "action.build"],
                  ["restore", "action.restore"],
                  NcmActions.isRunning("runclient")
                      ? ["stopclient", "action.stopClient"]
                      : ["runclient", "action.runClient"],
                  NcmActions.isRunning("runserver")
                      ? ["stopserver", "action.stopServer"]
                      : ["runserver", "action.runServer"],
                  ["clean", "action.clean"],
              ]
            : [["create", "view.create"]];

        const buttons = actions
            .map(
                ([action, key]) =>
                    `<button data-action="${action}">${PageView.escape(I18n.t(key))}</button>`
            )
            .join("\n        ");

        return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'nonce-${nonce}'; script-src 'nonce-${nonce}';">
<style nonce="${nonce}">
    body {
        font-family: var(--vscode-font-family);
        color: var(--vscode-foreground);
        padding: 8px;
        display: grid;
        gap: 6px;
    }
    button {
        width: 100%;
        background: var(--vscode-button-background);
        color: var(--vscode-button-foreground);
        border: none;
        padding: 6px 10px;
        font-size: 12px;
        cursor: pointer;
        text-align: left;
    }
    button:hover { background: var(--vscode-button-hoverBackground); }
    button.secondary {
        background: var(--vscode-button-secondaryBackground);
        color: var(--vscode-button-secondaryForeground);
    }
    button.secondary:hover { background: var(--vscode-button-secondaryHoverBackground); }
</style>
</head>
<body>
        ${buttons}
<script nonce="${nonce}">
    const vscode = acquireVsCodeApi();
    document.querySelectorAll("button[data-action]").forEach((node) => {
        node.addEventListener("click", () => {
            vscode.postMessage({ type: "action", action: node.dataset.action });
        });
    });
</script>
</body>
</html>`;
    }

    //escape keeps a translated label from breaking out of the markup
    private static escape(text: string): string {
        return text
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;");
    }
}
