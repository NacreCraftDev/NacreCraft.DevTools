//Ansi what the command line tool writes for a terminal, which a notification or a dialog cannot render
const pattern = /\u001b\[[0-9;]*[A-Za-z]/g;

//Strip removes the escape sequences, leaving the text they were coloring
export function stripAnsi(text: string): string {
    return text.replace(pattern, "");
}
