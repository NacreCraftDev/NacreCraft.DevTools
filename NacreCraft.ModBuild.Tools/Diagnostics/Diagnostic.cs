namespace NacreCraft.ModBuild.Diagnostics;

//DiagnosticSeverity the weight of a diagnostic
public enum DiagnosticSeverity
{
    //warns but lets the build pass
    Warning,
    //blocks the build
    Error,
}

//Diagnostic one diagnostic; file, line and column locate it for cargo-style line numbers and a caret
//Link is the official documentation url, shown only when no fix can be offered
public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string File,
    int Line,
    int Column,
    int Length,
    string SourceLine,
    string Note,
    string Link,
    IReadOnlyList<Suggestion> Fixes);

//DiagnosticBag every diagnostic collected in one check
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public int ErrorCount => _items.Count(item => item.Severity == DiagnosticSeverity.Error);

    public int WarningCount => _items.Count(item => item.Severity == DiagnosticSeverity.Warning);

    public bool HasErrors => ErrorCount > 0;

    //Add records a diagnostic, keeping only one copy per identical location and code
    //parsers occasionally report the same spot twice, and two identical blocks side by side are noise
    public void Add(Diagnostic diagnostic)
    {
        var key = $"{diagnostic.File}:{diagnostic.Line}:{diagnostic.Column}:{diagnostic.Code}:{diagnostic.Message}";
        if (!_seen.Add(key))
            return;

        _items.Add(diagnostic);
    }

    //Sorted orders by file and position so repeated runs over the same sources emit a stable sequence
    public IEnumerable<Diagnostic> Sorted()
        => _items
            .OrderBy(item => item.File, StringComparer.Ordinal)
            .ThenBy(item => item.Line)
            .ThenBy(item => item.Column);
}
