namespace NacreCraft.ModBuild.Core;

//ToolParameter, a command line parameter shown as a syntax and description pair in help
public sealed record ToolParameter(string Syntax, string Description);

//ToolEntry, a registered tool; Description shows beside the name in help and Parameters lists what it accepts, empty meaning none
public sealed record ToolEntry(
    string Name,
    string Description,
    IReadOnlyList<ToolParameter> Parameters,
    Func<string[], int> Run);

//ToolRegistry, where the program looks up a tool by the first command line argument
public static class ToolRegistry
{
    private static readonly List<ToolEntry> Entries = new();

    //Registered tools in registration order
    public static IReadOnlyList<ToolEntry> All => Entries;

    //Register a tool from its own Register method; parameters drive help only and each tool still parses its arguments
    public static void Register(string name, string description, Func<string[], int> run,
        params ToolParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(description);
        ArgumentNullException.ThrowIfNull(run);
        Entries.Add(new ToolEntry(name, description, parameters, run));
    }

    //Find a tool by name case-insensitively, null when absent
    public static ToolEntry? Find(string name)
        => Entries.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    //Subcommands such as view or example from the parameter list; bracketed positionals and dash options are excluded
    public static List<string> SubCommandsOf(ToolEntry tool)
        => tool.Parameters
            .Select(parameter => parameter.Syntax.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(word => !string.IsNullOrEmpty(word) && char.IsLetter(word[0]))
            .Select(word => word!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
