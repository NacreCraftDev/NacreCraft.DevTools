import { spawn } from "child_process";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";

//The values the creation dialog collects, in the order ncm init takes them
export interface CreateValues {
    name: string;
    id: string;
    description: string;
    authors: string;
    homepage: string;
    sources: string;
    license: string;
    //keep the template csproj as a .bak when the project is converted to .ncproj
    backup: boolean;
}

//ProjectCreator runs ncm init with the values the dialog collected
export class ProjectCreator {
    //Create returns the created directory, or null when ncm reported a failure
    public static async create(
        values: CreateValues
    ): Promise<string | null> {
        const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        if (!root) {
            vscode.window.showErrorMessage(
                "NacreCraft: open a folder first, a mod project needs one."
            );
            return null;
        }

        const empty = ProjectCreator.isEmpty(root);
        const args = [
            "init",
            values.name,
            values.id,
            values.description,
            values.authors,
            values.homepage,
            values.sources,
            values.license,
        ];

        //an empty folder gets the project in place, anything else gets a subdirectory named after the mod
        if (empty) {
            args.push("--here");
        }

        //ncm only reads .ncproj, so the fresh project is converted before it is opened
        args.push("--upgrade");
        if (!values.backup) {
            args.push("--no-backup");
        }

        const ok = await ProjectCreator.run(args, root);
        if (!ok) {
            return null;
        }

        return empty ? root : path.join(root, values.name);
    }

    //Run starts ncm and returns whether it succeeded, keeping its output for the failure case
    private static run(args: string[], cwd: string): Promise<boolean> {
        return new Promise((resolve) => {
            const child = spawn("ncm", args, {
                cwd,
                shell: true,
                windowsHide: true,
            });

            let output = "";
            child.stdout?.on("data", (chunk) => (output += chunk));
            child.stderr?.on("data", (chunk) => (output += chunk));

            child.on("error", (error) => {
                vscode.window.showErrorMessage(
                    `NacreCraft: ncm could not be started: ${error.message}`
                );
                resolve(false);
            });

            child.on("close", (code) => {
                if (code === 0) {
                    resolve(true);
                    return;
                }

                vscode.window.showErrorMessage(
                    `NacreCraft: ncm init failed (${code}). ${output.trim()}`
                );
                resolve(false);
            });
        });
    }

    //Empty whether the folder has nothing in it apart from editor metadata
    private static isEmpty(root: string): boolean {
        try {
            return fs
                .readdirSync(root)
                .every((name) => name === ".vscode" || name === ".git");
        } catch {
            return false;
        }
    }
}
