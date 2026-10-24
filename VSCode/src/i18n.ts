import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { Configuration } from "./config";

//I18n is the language subsystem, a sibling of the settings subsystem
//The language is resolved once at startup: a configured language wins, otherwise the editor's own language is used.
//Packs ship inside the extension and are unpacked into the extension storage, so the file the page reads is stable
//whether the extension runs packed or from a development folder
export class I18n {
    //Fallback pack every key has to exist in
    private static readonly fallback = "en";

    private static language = I18n.fallback;

    private static messages: Record<string, string> = {};

    //Language the session resolved to, useful for diagnostics
    public static get current(): string {
        return I18n.language;
    }

    //Initialize unpacks the packs and resolves the language for the first time
    public static async initialize(context: vscode.ExtensionContext): Promise<void> {
        await I18n.unpack(context, context.globalStorageUri.fsPath);
        await I18n.reload(context);
    }

    //Reload resolves the language and reads its pack
    //An installed pack whose key count no longer matches the shipped one is replaced first, so an older or edited
    //copy cannot leave the page with missing keys
    public static async reload(context: vscode.ExtensionContext): Promise<void> {
        const directory = context.globalStorageUri.fsPath;
        const available = I18n.refreshLanguages(context);

        //route one: a registered setting wins and the editor language is not consulted
        const configured = Configuration.get("language");
        let language = I18n.normalize(
            configured.length > 0 ? configured : vscode.env.language,
            available
        );

        if (I18n.stale(context, directory, language)) {
            language = await I18n.refresh(context, directory, language);
        }

        I18n.language = language;
        I18n.messages = await I18n.load(directory, language);
    }

    //Translate returns the text of a key, falling back to english and then to the key itself
    public static t(key: string): string {
        return I18n.messages[key] ?? key;
    }

    //Dump returns the whole active pack, which the page receives instead of asking for keys one by one
    public static dump(): Record<string, string> {
        return { ...I18n.messages };
    }

    //Refresh replaces an out of date pack and returns the language that ended up installed
    //When the extension does not ship the requested language the editor language is tried, and english last
    private static async refresh(
        context: vscode.ExtensionContext,
        directory: string,
        language: string
    ): Promise<string> {
        if (I18n.shipped(context, language)) {
            await I18n.copy(context, directory, language);
            return language;
        }

        const editor = I18n.normalize(
            vscode.env.language,
            I18n.languages(context)
        );
        if (editor !== language && I18n.shipped(context, editor)) {
            await I18n.copy(context, directory, editor);
            return editor;
        }

        //english is the one pack every build carries
        await I18n.copy(context, directory, I18n.fallback);
        return I18n.fallback;
    }

    //Stale whether the installed pack is missing or holds a different number of keys than the shipped one
    private static stale(
        context: vscode.ExtensionContext,
        directory: string,
        language: string
    ): boolean {
        const installed = I18n.readPack(
            path.join(directory, "locales", language + ".json")
        );
        const shipped = I18n.readPack(
            path.join(context.extensionPath, "locales", language + ".json")
        );

        //nothing shipped to compare against, the installed copy is all there is
        if (shipped === null) {
            return installed === null;
        }

        return (
            installed === null ||
            Object.keys(installed).length !== Object.keys(shipped).length
        );
    }

    //RefreshLanguages rewrites the language setting options from the packs on disk and returns them
    //Called whenever the settings page opens, so a pack dropped in while the extension runs is selectable
    public static refreshLanguages(context: vscode.ExtensionContext): string[] {
        const available = I18n.languages(context);
        Configuration.updatePresentation("language", {
            options: ["", ...available],
        });

        return available;
    }

    //Languages every pack name either the extension folder or the storage holds, english first
    public static languages(context: vscode.ExtensionContext): string[] {
        const names = new Set<string>(
            I18n.scan(path.join(context.extensionPath, "locales"))
        );

        for (const name of I18n.scan(
            path.join(context.globalStorageUri.fsPath, "locales")
        )) {
            names.add(name);
        }

        const rest = [...names]
            .filter((name) => name !== I18n.fallback)
            .sort();

        return [I18n.fallback, ...rest];
    }

    //Scan names the packs a directory holds
    private static scan(directory: string): string[] {
        try {
            return fs
                .readdirSync(directory)
                .filter((file) => file.toLowerCase().endsWith(".json"))
                .map((file) => file.slice(0, -".json".length).trim())
                .filter((name) => name.length > 0);
        } catch {
            //a directory that does not exist yet simply holds no packs
            return [];
        }
    }

    //Shipped whether the extension carries a pack for the language
    private static shipped(
        context: vscode.ExtensionContext,
        language: string
    ): boolean {
        return fs.existsSync(
            path.join(context.extensionPath, "locales", language + ".json")
        );
    }

    //Copy overwrites one installed pack with the shipped one
    private static async copy(
        context: vscode.ExtensionContext,
        directory: string,
        language: string
    ): Promise<void> {
        const source = path.join(
            context.extensionPath,
            "locales",
            language + ".json"
        );
        const target = path.join(directory, "locales", language + ".json");
        await fs.promises.mkdir(path.dirname(target), { recursive: true });
        await fs.promises.copyFile(source, target);
    }

    //ReadPack parses one pack file, null when it cannot be read
    private static readPack(file: string): Record<string, string> | null {
        try {
            return JSON.parse(fs.readFileSync(file, "utf8"));
        } catch {
            return null;
        }
    }

    //Unpack copies the packs into the storage directory, refreshing them when the extension version changes
    private static async unpack(
        context: vscode.ExtensionContext,
        directory: string
    ): Promise<void> {
        const source = path.join(context.extensionPath, "locales");
        const stamp = path.join(directory, ".locales-version");
        const version = String(context.extension.packageJSON.version ?? "0");

        if (
            fs.existsSync(stamp) &&
            fs.readFileSync(stamp, "utf8") === version
        ) {
            return;
        }

        await fs.promises.mkdir(directory, { recursive: true });
        await fs.promises.cp(source, path.join(directory, "locales"), {
            recursive: true,
            force: true,
        });
        await fs.promises.writeFile(stamp, version);
    }

    //Load reads one pack, falling back to english when the language has none
    private static async load(
        directory: string,
        language: string
    ): Promise<Record<string, string>> {
        const packs = path.join(directory, "locales");
        const requested = path.join(packs, language + ".json");
        const file = fs.existsSync(requested)
            ? requested
            : path.join(packs, I18n.fallback + ".json");

        try {
            return JSON.parse(await fs.promises.readFile(file, "utf8"));
        } catch {
            return {};
        }
    }

    //Normalize maps an editor language onto one of the packs actually present, english when nothing matches
    private static normalize(language: string, available: string[]): string {
        const lower = language.toLowerCase();

        const exact = available.find((name) => name.toLowerCase() === lower);
        if (exact) {
            return exact;
        }

        //an editor tag such as ja-jp or pt-br lands on the pack sharing its prefix
        const prefixed = available.find((name) =>
            lower.startsWith(name.toLowerCase() + "-")
        );
        if (prefixed) {
            return prefixed;
        }

        //chinese editor tags differ by script or region while the extension may carry a single pack
        if (lower.startsWith("zh")) {
            return (
                available.find((name) => name.toLowerCase() === "zh-cn") ??
                I18n.fallback
            );
        }

        return I18n.fallback;
    }
}
