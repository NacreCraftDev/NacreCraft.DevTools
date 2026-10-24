namespace NacreCraft.ModBuild.Diagnostics;

//Applicability how much a suggestion can be trusted, aligned with rustc's four levels
public enum Applicability
{
    //applying it is always correct, so a machine can do so directly
    MachineApplicable,
    //the shape is right but placeholders still need a person to fill them
    HasPlaceholders,
    //a guess that needs a person to confirm
    MaybeIncorrect,
    //undetermined
    Unspecified,
}

//Suggestion one fix suggestion
//one carrying a Replacement renders as corrected code while a message-only one is plain text
//positions use 1-based line and column to match Diagnostic
public sealed record Suggestion(
    string Message,
    int Line,
    int Column,
    int Length,
    string? Replacement,
    Applicability Applicability)
{
    //Replaceable reports whether this suggestion can supply replacement text
    public bool Replaceable => Replacement is not null && Length > 0;

    //Text builds a plain-text suggestion with no location in the source
    public static Suggestion Text(string message)
        => new(message, 0, 0, 0, null, Applicability.Unspecified);

    //Replace builds a replacement suggestion pointing at a span of a line
    public static Suggestion Replace(
        string message,
        int line,
        int column,
        int length,
        string replacement,
        Applicability applicability)
        => new(message, line, column, length, replacement, applicability);
}
