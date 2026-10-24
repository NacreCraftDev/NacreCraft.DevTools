import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";

//ConfigDefinition what a subsystem declares about one setting
//label and description are translation keys, resolved when the page renders
export interface ConfigDefinition {
    key: string;
    defaultValue: string;
    options?: string[];
    label: string;
    description: string;
}

//Configuration the settings subsystem
//Settings are registered up front, then loaded from a json file owned by the extension. A document that cannot be
//parsed is backed up and rebuilt, and a value that does not fit its declaration falls back to the default of that
//key alone, leaving the rest of the table untouched
export class Configuration {
    private static readonly definitions = new Map<string, ConfigDefinition>();

    private static readonly values = new Map<string, string>();

    private static readonly changedEmitter = new vscode.EventEmitter<string>();

    private static file = "";

    //Fires with the key that changed, so a listener can leave the heavy work for the settings that need it
    public static readonly changed = Configuration.changedEmitter.event;

    //Path of the document holding the values
    public static get path(): string {
        return Configuration.file;
    }

    //Register declares a setting; the key itself cannot change afterwards
    public static register(definition: ConfigDefinition): void {
        if (Configuration.definitions.has(definition.key)) {
            throw new Error(`setting ${definition.key} is already registered`);
        }

        Configuration.definitions.set(definition.key, definition);
        Configuration.values.set(definition.key, definition.defaultValue);
    }

    //Initialize loads the document, repairing each value and rebuilding one that is broken
    public static initialize(context: vscode.ExtensionContext): void {
        Configuration.file = path.join(
            context.globalStorageUri.fsPath,
            "config.json"
        );
        fs.mkdirSync(path.dirname(Configuration.file), { recursive: true });

        const stored = Configuration.read();
        for (const definition of Configuration.definitions.values()) {
            Configuration.values.set(
                definition.key,
                Configuration.accept(definition, stored[definition.key])
            );
        }

        Configuration.write();
    }

    //Get reads the current value of a key
    public static get(key: string): string {
        return Configuration.values.get(key) ?? "";
    }

    //Set writes a value, falling back to the default of that key when it does not fit
    public static set(key: string, value: unknown): void {
        const definition = Configuration.definitions.get(key);
        if (!definition) {
            return;
        }

        const accepted = Configuration.accept(definition, value);
        if (Configuration.values.get(key) === accepted) {
            return;
        }

        Configuration.values.set(key, accepted);
        Configuration.write();
        Configuration.changedEmitter.fire(key);
    }

    //UpdatePresentation refreshes the text of a key, which a language change uses
    //The key and its default value stay as registered
    public static updatePresentation(
        key: string,
        patch: Partial<Pick<ConfigDefinition, "label" | "description" | "options">>
    ): void {
        const definition = Configuration.definitions.get(key);
        if (!definition) {
            return;
        }

        Configuration.definitions.set(key, { ...definition, ...patch });
    }

    //All returns every definition with its current value, in registration order
    public static all(): Array<ConfigDefinition & { value: string }> {
        return [...Configuration.definitions.values()].map((definition) => ({
            ...definition,
            value:
                Configuration.values.get(definition.key) ??
                definition.defaultValue,
        }));
    }

    //accept keeps a value that fits the declaration and returns the default otherwise
    private static accept(definition: ConfigDefinition, value: unknown): string {
        if (typeof value !== "string") {
            return definition.defaultValue;
        }

        if (
            definition.options &&
            definition.options.length > 0 &&
            !definition.options.includes(value)
        ) {
            return definition.defaultValue;
        }

        return value;
    }

    //read parses the document, backing it up and starting over when it cannot be used
    private static read(): Record<string, unknown> {
        if (!fs.existsSync(Configuration.file)) {
            return {};
        }

        try {
            const text = fs.readFileSync(Configuration.file, "utf8");
            const parsed = JSON.parse(text);
            if (
                parsed !== null &&
                typeof parsed === "object" &&
                !Array.isArray(parsed)
            ) {
                return parsed as Record<string, unknown>;
            }
        } catch {
            //the backup below is the only recovery for a document that cannot be parsed
        }

        Configuration.backup();
        return {};
    }

    //backup keeps the broken document beside the new one, stamped so nothing is overwritten
    private static backup(): void {
        const stamp = new Date().toISOString().replace(/[:.]/g, "-");
        try {
            fs.copyFileSync(
                Configuration.file,
                `${Configuration.file}.${stamp}.bak`
            );
        } catch {
            //the file may have vanished since it was read, nothing to keep then
        }
    }

    //write persists the whole table
    private static write(): void {
        const payload: Record<string, string> = {};
        for (const [key, value] of Configuration.values) {
            payload[key] = value;
        }

        fs.writeFileSync(
            Configuration.file,
            JSON.stringify(payload, null, 2) + "\n"
        );
    }
}
