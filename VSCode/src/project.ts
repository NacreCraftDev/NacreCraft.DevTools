import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";

//HookInfo one injection rule as ncmod.json declares it
export interface HookInfo {
    target: string;
    method: string;
    type: string;
    replaceType: string;
    replaceMethod: string;
}

//MixinInfo one mixin as ncmod.json declares it
export interface MixinInfo {
    target: string;
    source: string;
}

//ModProjectInfo what the page and the side bar show about the open mod
export interface ModProjectInfo {
    root: string;
    name: string;
    id: string;
    version: string;
    description: string;
    environment: string;
    entry: string;
    //absolute path of the icon the manifest names, null when it names none or the file is missing
    icon: string | null;
    hooks: HookInfo[];
    mixins: MixinInfo[];
}

//ProjectLocator finds the mod project of the current workspace
//A workspace is a mod workspace when its root holds a .ncproj; anything else is left alone without a message
export class ProjectLocator {
    //Find returns the project of the first workspace folder, or null when this is not a mod workspace
    public static find(): ModProjectInfo | null {
        const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        if (!root) {
            return null;
        }

        if (!ProjectLocator.hasProjectFile(root)) {
            return null;
        }

        const manifest = path.join(root, "ncmod.json");
        if (!fs.existsSync(manifest)) {
            //a bootstrap config aggregates other projects and needs no manifest of its own
            if (!ProjectLocator.hasBootstrap(root)) {
                return null;
            }

            return {
                root,
                name: path.basename(root),
                id: "",
                version: "",
                description: "",
                environment: "both",
                entry: "",
                icon: null,
                hooks: [],
                mixins: [],
            };
        }

        try {
            const data = JSON.parse(fs.readFileSync(manifest, "utf8"));
            return {
                root,
                name: String(data.name ?? path.basename(root)),
                id: String(data.id ?? ""),
                version: String(data.version ?? ""),
                description: String(data.description ?? ""),
                environment: String(data.environment ?? "both"),
                entry: String(data.entry ?? ""),
                icon: ProjectLocator.icon(root, data.icon),
                hooks: ProjectLocator.hooks(data.hooks),
                mixins: ProjectLocator.mixins(data.mixins),
            };
        } catch {
            //a broken manifest is the language server's problem to report, the page shows no project
            return null;
        }
    }

    //HasProjectFile whether the directory holds a project file
    public static hasProjectFile(root: string): boolean {
        try {
            return fs
                .readdirSync(root)
                .some((name) => name.endsWith(".ncproj"));
        } catch {
            return false;
        }
    }

    //HasBootstrap whether the root config only aggregates other projects
    private static hasBootstrap(root: string): boolean {
        try {
            const config = fs
                .readdirSync(root)
                .find((name) => name.endsWith(".ncproj"));
            if (!config) {
                return false;
            }

            const text = fs.readFileSync(path.join(root, config), "utf8");
            return /Bootstrap\s*=\s*["']true["']/i.test(text);
        } catch {
            return false;
        }
    }

    //Icon resolves the icon name to a file that is really there
    private static icon(root: string, value: unknown): string | null {
        if (typeof value !== "string" || value.length === 0) {
            return null;
        }

        const file = path.join(root, value);
        return fs.existsSync(file) ? file : null;
    }

    //Hooks reads the hooks array, keeping the fields the page shows
    private static hooks(value: unknown): HookInfo[] {
        if (!Array.isArray(value)) {
            return [];
        }

        return value
            .filter((item) => item !== null && typeof item === "object")
            .map((item) => ({
                target: String(item.target ?? ""),
                method: String(item.method ?? ""),
                type: String(item.type ?? "CallSite"),
                replaceType: String(item.replaceType ?? ""),
                replaceMethod: String(item.replaceMethod ?? ""),
            }));
    }

    //Mixins reads the mixins array, keeping the fields the page shows
    private static mixins(value: unknown): MixinInfo[] {
        if (!Array.isArray(value)) {
            return [];
        }

        return value
            .filter((item) => item !== null && typeof item === "object")
            .map((item) => ({
                target: String(item.target ?? ""),
                source: String(item.source ?? ""),
            }));
    }
}
