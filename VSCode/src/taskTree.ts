import * as vscode from "vscode";
import { NcmActions, TaskEntry } from "./actions";
import { I18n } from "./i18n";
import { ProjectLocator } from "./project";

//TaskTree the side bar view listing the project's own tasks
//The list is read from the language server, so the task block stays the single source of truth
export class TaskTree implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();

    private items: TaskEntry[] = [];

    private error = "";

    private loaded = false;

    private loading: Promise<void> | undefined;

    public readonly onDidChangeTreeData = this.emitter.event;

    //Refresh asks the server again, which is what every config write ends with
    public refresh(): void {
        this.loaded = false;
        void this.load();
    }

    public getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    public async getChildren(): Promise<vscode.TreeItem[]> {
        if (ProjectLocator.find() === null) {
            return [];
        }

        if (!this.loaded) {
            await this.load();
        }

        if (this.error.length > 0) {
            return [TaskTree.message(this.error, "warning")];
        }

        if (this.items.length === 0) {
            return [TaskTree.message(I18n.t("tasks.empty"), "info")];
        }

        return this.items.map((entry) => new TaskNode(entry));
    }

    //load asks the language server for the task list, one request at a time
    private async load(): Promise<void> {
        if (this.loading) {
            await this.loading;
            return;
        }

        this.loading = this.fetch();
        try {
            await this.loading;
        } finally {
            this.loading = undefined;
            this.emitter.fire();
        }
    }

    private async fetch(): Promise<void> {
        if (ProjectLocator.find() === null || !NcmActions.ready) {
            this.items = [];
            this.error = "";
            this.loaded = true;
            return;
        }

        const result = await NcmActions.tasks();
        this.items = result.ok ? result.tasks ?? [] : [];
        this.error = result.ok ? "" : result.error ?? "";
        this.loaded = true;
    }

    //message is the single row shown while the list is empty or unreadable
    private static message(text: string, icon: string): vscode.TreeItem {
        const item = new vscode.TreeItem(
            text,
            vscode.TreeItemCollapsibleState.None
        );
        item.iconPath = new vscode.ThemeIcon(icon);
        item.contextValue = "nacrecraft.taskMessage";
        return item;
    }
}

//TaskNode one task, whose click runs it through the same path the page uses
class TaskNode extends vscode.TreeItem {
    constructor(public readonly entry: TaskEntry) {
        super(entry.name, vscode.TreeItemCollapsibleState.None);

        this.description = entry.description || entry.steps.join(" | ");
        this.tooltip = TaskNode.tooltip(entry);
        this.contextValue = "nacrecraft.task";
        this.iconPath = new vscode.ThemeIcon(
            entry.shadowed ? "warning" : entry.overrides ? "play-circle" : "play"
        );

        this.command = {
            command: "nacrecraft.runTask",
            title: I18n.t("tasks.sidebar.run"),
            arguments: [entry.name],
        };
    }

    //tooltip carries what the row has no room for
    private static tooltip(entry: TaskEntry): string {
        const lines = [entry.name];
        if (entry.description.length > 0) {
            lines.push(entry.description);
        }
        if (entry.depends.length > 0) {
            lines.push(
                I18n.t("tasks.depends") + ": " + entry.depends.join(", ")
            );
        }
        if (entry.steps.length > 0) {
            lines.push(entry.steps.join("\n"));
        }
        if (entry.shadowed) {
            lines.push(I18n.t("tasks.shadowed"));
        }

        return lines.join("\n");
    }
}
