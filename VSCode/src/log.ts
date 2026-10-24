import * as vscode from "vscode";

//LogEntry one line in the page's log panel
export interface LogEntry {
    time: string;
    level: "info" | "warn" | "error";
    message: string;
}

//Log collects what the extension and the language server report, and what is running right now
//The page owns the presentation: this only keeps the tail and tells it when something changed
export class Log {
    //How many lines are kept, older ones are dropped so a long session cannot grow without bound
    private static readonly limit = 500;

    private static readonly entries: LogEntry[] = [];

    private static readonly emitter = new vscode.EventEmitter<void>();

    private static status = "";

    //Fires whenever a line is added or the status changes
    public static readonly changed = Log.emitter.event;

    public static info(message: string): void {
        Log.add("info", message);
    }

    public static warn(message: string): void {
        Log.add("warn", message);
    }

    public static error(message: string): void {
        Log.add("error", message);
    }

    //SetStatus names the operation in flight, an empty string returns the line to its resting text
    public static setStatus(message: string): void {
        Log.status = message;
        Log.emitter.fire();
    }

    //Snapshot what the page renders
    public static snapshot(): { status: string; entries: LogEntry[] } {
        return { status: Log.status, entries: [...Log.entries] };
    }

    private static add(level: LogEntry["level"], message: string): void {
        Log.entries.push({
            time: new Date().toLocaleTimeString(),
            level,
            message,
        });

        if (Log.entries.length > Log.limit) {
            Log.entries.splice(0, Log.entries.length - Log.limit);
        }

        Log.emitter.fire();
    }
}
