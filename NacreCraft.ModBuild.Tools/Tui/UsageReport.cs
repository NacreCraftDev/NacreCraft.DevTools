using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;
using Spectre.Console;

namespace NacreCraft.ModBuild.Tui;

//The result of one scan, rendered into the summary, problem table and detail views
//Shared by the interactive tui and the one-shot redirected report so both show the same thing
internal sealed class UsageReport
{
    private UsageReport(string root, List<ApiUsageItem> items, int fileCount)
    {
        Root = root;
        Items = items;
        FileCount = fileCount;
        Problems = items.Where(item => item.State != UsageState.Ok).ToList();
    }

    //Directory that was scanned
    public string Root { get; }

    //All usages, problems first
    public IReadOnlyList<ApiUsageItem> Items { get; }

    //Usages that need attention, i.e. not Ok
    public IReadOnlyList<ApiUsageItem> Problems { get; }

    public int FileCount { get; }

    public int ErrorCount => Items.Count(item => item.State == UsageState.Missing);

    public int WarningCount => Items.Count(item => item.State == UsageState.Warn);

    public int OkCount => Items.Count(item => item.State == UsageState.Ok);

    //Scans a directory, ordering problems first and then by symbol name
    public static UsageReport Scan(string root, TemplateCatalog catalog)
    {
        var items = ApiUsage.Scan(root, catalog).Items
            .OrderBy(item => item.State == UsageState.Ok ? 1 : 0)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal)
            .ToList();

        return new UsageReport(root, items, SourceFiles.Enumerate(root).Count());
    }

    public void RenderSummary(string title)
    {
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(title)}[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            $"[grey]{FileCount} file(s) scanned[/]   "
            + $"[red]{ErrorCount} error(s)[/]   "
            + $"[yellow]{WarningCount} warning(s)[/]   "
            + $"[green]{OkCount} ok[/]");
        AnsiConsole.WriteLine();
    }

    //Prints a single line when there is nothing to report
    public void RenderTable()
    {
        if (Problems.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No api usage problem found[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("#")
            .AddColumn("level")
            .AddColumn("symbol")
            .AddColumn("where")
            .AddColumn("fix hints");

        for (var index = 0; index < Problems.Count; index++)
        {
            var item = Problems[index];
            table.AddRow(
                new Markup($"[grey]{index + 1}[/]"),
                new Markup($"[{Color(item.State)}]{Label(item.State)}[/]"),
                new Markup(Markup.Escape(item.Symbol)),
                new Markup(Markup.Escape(Where(item))),
                new Markup(Hints(item)));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    //Shows one usage in full, with every source line and candidate
    public void RenderDetail(ApiUsageItem item)
    {
        var lines = new List<string>
        {
            $"[bold]{Markup.Escape(item.Symbol)}[/]",
            $"[grey]level[/]    [{Color(item.State)}]{Label(item.State)}[/]",
            $"[grey]type[/]     {Markup.Escape(item.Type)}",
            $"[grey]member[/]   {(string.IsNullOrWhiteSpace(item.Member) ? "[grey]-[/]" : Markup.Escape(item.Member))}",
        };

        if (item.Locations.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("[grey]where[/]");
            foreach (var location in item.Locations)
            {
                //Paste the source line as-is and keep the indentation, so the location can be checked at a glance
                lines.Add($"  [blue]{Markup.Escape(location.File)}:{location.Line}[/]");
                lines.Add($"    [grey]{Markup.Escape(location.Text.TrimEnd())}[/]");
            }
        }

        if (item.Candidates.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("[grey]candidates[/]");
            foreach (var candidate in item.Candidates)
                lines.Add($"  [green]{Markup.Escape(candidate)}[/]");
        }

        AnsiConsole.Write(new Panel(new Markup(string.Join('\n', lines)))
            .Header("[bold]usage detail[/]")
            .Border(BoxBorder.Rounded)
            .Expand());
        AnsiConsole.WriteLine();
    }

    //Colors match the diagnostics output
    private static string Color(UsageState state) => state switch
    {
        UsageState.Missing => "red",
        UsageState.Warn => "yellow",
        _ => "green",
    };

    //Labels line up with the diagnostics error/warning wording
    private static string Label(UsageState state) => state switch
    {
        UsageState.Missing => "error",
        UsageState.Warn => "warning",
        _ => "ok",
    };

    //First location, with a count appended when there are more
    private static string Where(ApiUsageItem item)
    {
        if (item.Locations.Count == 0)
            return "?";

        var first = item.Locations[0];
        var text = $"{first.File}:{first.Line}";
        return item.Locations.Count > 1 ? $"{text} (+{item.Locations.Count - 1})" : text;
    }

    //The table keeps only the first few candidates, the detail view has the rest
    private static string Hints(ApiUsageItem item)
        => item.Candidates.Count == 0
            ? "[grey]-[/]"
            : Markup.Escape(string.Join(", ", item.Candidates.Take(3)));
}
