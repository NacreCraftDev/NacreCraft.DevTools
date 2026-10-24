import { ChildProcess, spawn } from "child_process";
import * as vscode from "vscode";
import { stripAnsi } from "./ansi";
import { Log } from "./log";

//NcmResult what one command line run ended with
export interface NcmResult {
    ok: boolean;
    output: string;
}

//Ncm starts the command line tool in the workspace root
//The output is streamed into the log and into the progress notification, which is where a long build says what it is doing
export class Ncm {
    //Run returns once the process exits, the caller decides what to say about it
    //onStart hands the process over, which a launch needs to talk to it while it runs
    public static run(
        args: string[],
        title: string,
        onStart?: (child: ChildProcess) => void
    ): Thenable<NcmResult> {
        const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
        if (!root) {
            return Promise.resolve({ ok: false, output: "no workspace folder is open" });
        }

        return vscode.window.withProgress(
            {
                location: vscode.ProgressLocation.Notification,
                title,
                //a build cannot be undone halfway, and a running client ends by closing the game
                cancellable: false,
            },
            (progress) =>
                new Promise<NcmResult>((resolve) => {
                    Log.info("ncm " + args.join(" "));

                    //no shell: the process has to stay the one we can talk to and end
                    const child = spawn("ncm", args, {
                        cwd: root,
                        windowsHide: true,
                    });

                    onStart?.(child);

                    let output = "";
                    let partial = "";

                    const emit = (line: string): void => {
                        output += line + "\n";
                        if (line.trim().length === 0) {
                            return;
                        }

                        Log.info(line);
                        progress.report({ message: Ncm.shorten(line) });
                    };

                    const feed = (chunk: Buffer): void => {
                        partial += chunk.toString();
                        let index = partial.indexOf("\n");
                        while (index >= 0) {
                            emit(partial.slice(0, index).replace(/\r$/, ""));
                            partial = partial.slice(index + 1);
                            index = partial.indexOf("\n");
                        }
                    };

                    child.stdout?.on("data", feed);
                    child.stderr?.on("data", feed);

                    child.on("error", (error) => {
                        resolve({ ok: false, output: output + error.message });
                    });

                    child.on("close", (code) => {
                        if (partial.length > 0) {
                            emit(partial);
                        }

                        resolve({ ok: code === 0, output });
                    });
                })
        );
    }

    //progress text has one line, so a very long message is cut instead of wrapping the notification
    private static shorten(line: string): string {
        const text = stripAnsi(line).replace(/\t/g, " ").trim();
        return text.length > 120 ? text.slice(0, 117) + "..." : text;
    }
}
