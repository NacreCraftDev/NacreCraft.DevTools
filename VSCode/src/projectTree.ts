import * as vscode from "vscode";
import { I18n } from "./i18n";
import { ProjectLocator } from "./project";

//ProjectTree the side bar view showing the open mod
//It keeps no state of its own: every refresh asks the locator again, so the view cannot drift from disk
export class ProjectTree implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();

    public readonly onDidChangeTreeData = this.emitter.event;

    //Refresh re-reads the workspace
    public refresh(): void {
        this.emitter.fire();
    }

    public getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    public getChildren(): vscode.TreeItem[] {
        const project = ProjectLocator.find();
        if (project === null) {
            return [];
        }

        return [
            ProjectTree.row("project.name", project.name),
            ProjectTree.row("project.id", project.id),
            ProjectTree.row("project.version", project.version),
            ProjectTree.row("project.environment", project.environment),
            ProjectTree.row("project.entry", project.entry),
            ProjectTree.row("project.root", project.root),
        ];
    }

    private static row(key: string, value: string): vscode.TreeItem {
        const item = new vscode.TreeItem(
            I18n.t(key),
            vscode.TreeItemCollapsibleState.None
        );
        item.description = value;
        item.tooltip = value;
        return item;
    }
}
