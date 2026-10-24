using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;
using NacreCraft.ModBuild.Tools;
using Spectre.Console;

namespace NacreCraft.ModBuild.Tui;

//Terminal ui for scanning api usage in a project
//Keeps a single flow of summary, problem table and detail, presenting what ApiUsage already decided
//Falls back to a one-shot report when input or output is redirected, so scripts and ci can still run it
internal static class UsageTui
{
    public static int Run()
    {
        var catalog = TemplateStore.LoadCatalog();
        if (catalog is null)
        {
            AnsiConsole.MarkupLine("[red]error[/] the template catalog is unavailable, the api check needs it");
            return 1;
        }

        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null)
        {
            AnsiConsole.MarkupLine($"[red]error[/] no {ModProject.ManifestName} in this directory or any parent");
            return 1;
        }

        var root = Path.GetDirectoryName(project.ManifestPath)!;

        //Exclusions apply to the source scan, so they have to be in place before the panel enumerates
        SourceFiles.Configure(NcProject.TryFind(root, out _));
        return IsInteractive() ? Loop(root, catalog, project) : Report(root, catalog, project);
    }

    private static bool IsInteractive()
        => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    //Returns non-zero on errors so it can gate a build script
    private static int Report(string root, TemplateCatalog catalog, ModProject project)
    {
        var report = UsageReport.Scan(root, catalog);
        report.RenderSummary(project.DisplayName);
        report.RenderTable();
        return report.ErrorCount > 0 ? 1 : 0;
    }

    //Scans once and lets you browse, only a rescan walks the source again
    private static int Loop(string root, TemplateCatalog catalog, ModProject project)
    {
        var report = ScanWithStatus(root, catalog, project);

        while (true)
        {
            report.RenderSummary(project.DisplayName);
            report.RenderTable();

            var entry = AskEntry(report);
            if (entry.Action == EntryAction.Quit)
                return 0;
            if (entry.Action == EntryAction.Rescan)
            {
                report = ScanWithStatus(root, catalog, project);
                continue;
            }

            var usage = entry.Usage!;
            report.RenderDetail(usage);

            switch (AskDetail())
            {
                case DetailAction.Back:
                    break;
                case DetailAction.Rescan:
                    report = ScanWithStatus(root, catalog, project);
                    break;
                case DetailAction.Quit:
                    return 0;
                case DetailAction.WriteExample:
                    WriteExample(catalog, usage);
                    break;
            }
        }
    }

    //A spinner is needed here because reading the whole project source otherwise looks like a hang
    private static UsageReport ScanWithStatus(string root, TemplateCatalog catalog, ModProject project)
        => AnsiConsole.Status()
            .Start($"scanning {project.DisplayName}...", _ => UsageReport.Scan(root, catalog))!;

    private static Entry AskEntry(UsageReport report)
    {
        var entries = new List<Entry>();
        for (var index = 0; index < report.Problems.Count; index++)
        {
            var item = report.Problems[index];
            entries.Add(new Entry(EntryAction.Open, $"#{index + 1}  {item.Symbol}", item));
        }

        entries.Add(new Entry(EntryAction.Rescan, "rescan the project", null));
        entries.Add(new Entry(EntryAction.Quit, "quit", null));

        return AnsiConsole.Prompt(new SelectionPrompt<Entry>()
            .Title("[grey]pick an api usage to inspect[/]")
            .PageSize(15)
            .UseConverter(entry => Markup.Escape(entry.Label))
            .AddChoices(entries));
    }

    private static DetailAction AskDetail()
        => AnsiConsole.Prompt(new SelectionPrompt<DetailAction>()
            .Title("[grey]what next[/]")
            .UseConverter(action => action switch
            {
                DetailAction.WriteExample => "pull the matching template into the current directory",
                DetailAction.Back => "back to the list",
                DetailAction.Rescan => "rescan the project",
                _ => "quit",
            })
            .AddChoices(
                DetailAction.WriteExample,
                DetailAction.Back,
                DetailAction.Rescan,
                DetailAction.Quit));

    //Pulls catalog entries matching a candidate into the current directory
    //Candidates use code names while the catalog uses fully qualified ids, so the two line up on the type name
    private static void WriteExample(TemplateCatalog catalog, ApiUsageItem usage)
    {
        var matches = MatchingEntries(catalog, usage);
        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]warning[/] no candidate of this usage has a template entry in the catalog");
            return;
        }

        var entry = matches.Count == 1
            ? matches[0]
            : AnsiConsole.Prompt(new SelectionPrompt<TemplateEntry>()
                .Title("[grey]pick a template to pull[/]")
                .PageSize(12)
                .UseConverter(item => $"{Markup.Escape(item.Id)}  [grey]{Markup.Escape(item.Title)}[/]")
                .AddChoices(matches));

        TemplateTool.PullExample(catalog, entry);
    }

    //Finds catalog entries by candidate type name, falling back to the usage's own type when there are no candidates
    private static List<TemplateEntry> MatchingEntries(TemplateCatalog catalog, ApiUsageItem usage)
    {
        var names = usage.Candidates
            .Select(TypeNameOf)
            .Append(usage.Type)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        return catalog.Apis
            .Where(entry => names.Contains(LastSegment(entry.Id)))
            .ToList();
    }

    //A candidate is written as Type.Member or Type, so take the leading segment
    private static string TypeNameOf(string candidate)
    {
        var dot = candidate.IndexOf('.');
        return dot < 0 ? candidate : candidate[..dot];
    }

    //The last segment of a catalog id is the type name written in code
    private static string LastSegment(string id)
    {
        var dot = id.LastIndexOf('.');
        return dot < 0 ? id : id[(dot + 1)..];
    }

    private enum EntryAction
    {
        Open,
        Rescan,
        Quit,
    }

    private enum DetailAction
    {
        WriteExample,
        Back,
        Rescan,
        Quit,
    }

    //A row in the main menu, a non-null Usage means it opens that usage's detail
    private sealed record Entry(EntryAction Action, string Label, ApiUsageItem? Usage);
}
