using System.Globalization;

namespace NacreCraft.ModBuild.Diagnostics;

//DiagnosticRenderer draws diagnostics as rustc-style blocks
//each block shows the position line, a caret, then the message and any fixes
public static class DiagnosticRenderer
{
    //Render writes every diagnostic in order
    public static void Render(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
            RenderOne(diagnostic);
    }

    private static void RenderOne(Diagnostic diagnostic)
    {
        var severity = diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";
        var color = diagnostic.Severity == DiagnosticSeverity.Error ? ConsoleColor.Red : ConsoleColor.Yellow;

        //the gutter width follows the line number so the following pipes line up
        var pad = new string(' ', diagnostic.Line.ToString(CultureInfo.InvariantCulture).Length);

        WriteColored($"{severity}[{diagnostic.Code}]", color);
        Console.WriteLine($": {diagnostic.Message}");

        Console.WriteLine($"{pad}--> {diagnostic.File}:{diagnostic.Line}:{diagnostic.Column}");

        if (!string.IsNullOrEmpty(diagnostic.SourceLine))
        {
            Console.WriteLine($"{pad} |");
            Console.WriteLine($"{diagnostic.Line} | {diagnostic.SourceLine}");
            Console.WriteLine($"{pad} | {Indent(diagnostic.Column)}{new string('^', Math.Max(1, diagnostic.Length))}");
        }

        if (!string.IsNullOrWhiteSpace(diagnostic.Note))
        {
            Console.WriteLine($"{pad} |");
            Console.WriteLine($"{pad} = {diagnostic.Note}");
        }

        //fixes with a location render as a before and after line
        foreach (var fix in diagnostic.Fixes)
        {
            if (fix.Replaceable)
                RenderReplacement(pad, diagnostic, fix);
        }

        //text-only fixes are listed as-is, without an extra heading
        var advice = diagnostic.Fixes.Where(fix => !fix.Replaceable).ToList();
        if (advice.Count > 0)
        {
            Console.WriteLine($"{pad} |");
            foreach (var fix in advice)
                Console.WriteLine($"{pad} = {fix.Message}");
        }
        //the documentation url is the fallback only when nothing else was offered, counting replaceable fixes as offered
        else if (diagnostic.Fixes.Count == 0 && !string.IsNullOrWhiteSpace(diagnostic.Link))
        {
            Console.WriteLine($"{pad} |");
            Console.WriteLine($"{pad} = {LinkText(diagnostic.Link)}");
        }

        Console.WriteLine();
    }

    //RenderReplacement draws one replacement fix with the original line marked minus and the updated line marked plus
    private static void RenderReplacement(string pad, Diagnostic diagnostic, Suggestion fix)
    {
        //a fix on another line has no source text here, so fall back to plain text rather than showing mismatched code
        if (fix.Line != diagnostic.Line || string.IsNullOrEmpty(diagnostic.SourceLine))
        {
            Console.WriteLine($"{pad} = {fix.Message}");
            return;
        }

        var updated = Replace(diagnostic.SourceLine, fix);
        if (updated is null)
        {
            Console.WriteLine($"{pad} = {fix.Message}");
            return;
        }

        var gutter = fix.Line.ToString(CultureInfo.InvariantCulture).PadLeft(pad.Length);

        WriteColored("help", ConsoleColor.Cyan);
        Console.WriteLine($": {fix.Message}");
        Console.WriteLine($"{pad} |");

        WriteColored($"{gutter} - {diagnostic.SourceLine}", ConsoleColor.Red);
        Console.WriteLine();
        WriteColored($"{gutter} + {updated}", ConsoleColor.Green);
        Console.WriteLine();
    }

    //Replace swaps the fix's replacement text into the line range
    private static string? Replace(string line, Suggestion fix)
    {
        var start = fix.Column - 1;
        if (start < 0 || start > line.Length)
            return null;

        var end = start + fix.Length;
        if (end > line.Length)
            return null;

        return line[..start] + fix.Replacement + line[end..];
    }

    //LinkText renders the documentation url as clickable terminal text
    //when the terminal does not support links it falls back to plain text carrying the url
    private static string LinkText(string url)
        => SupportsLinks()
            ? $"\u001b[36m\u001b[4m\u001b]8;;{url}\u001b\\docs\u001b]8;;\u001b\\\u001b[0m"
            : $"docs: {url}";

    //SupportsLinks reports whether the terminal handles OSC 8 hyperlinks
    //redirected output disables them so escape sequences never land in a file
    private static bool SupportsLinks()
    {
        if (Console.IsOutputRedirected)
            return false;

        var term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrEmpty(term) && !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
            return true;

        //Windows sets no TERM, so these markers identify Windows Terminal and VS Code
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM_PROGRAM"));
    }

    //Indent converts a 1-based column into the spaces that precede the caret
    private static string Indent(int column)
        => column <= 1 ? string.Empty : new string(' ', column - 1);

    //WriteColored writes in a temporary color and restores the previous one
    private static void WriteColored(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
