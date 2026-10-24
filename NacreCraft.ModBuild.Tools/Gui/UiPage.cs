using System.Text.Json;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Gui;

//Builds the panel page
//The document and usage table are prepared before the window opens and written straight into a json variable in the page, avoiding a message bridge round trip
internal static class UiPage
{
    private const string DataToken = "/*DATA*/";

    private const string MarkedToken = "/*MARKED*/";

    private static readonly Lazy<string> Page = new(() => Resource("ui.html"));
    private static readonly Lazy<string> Marked = new(() => Resource("marked.min.js"));

    //The page reads lower camel case fields, so serialization converts names while keys and strings stay untouched
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Build(TemplateCatalog? catalog)
    {
        var data = JsonSerializer.Serialize(new Payload(LoadDocument(catalog), ScanUsage(catalog)), Json);
        Trace.Log($"page payload {data.Length} chars");
        return Page.Value.Replace(MarkedToken, Marked.Value).Replace(DataToken, data);
    }

    //Scans the source when inside a mod project and returns an empty list otherwise, which just hides the sidebar
    private static List<Usage> ScanUsage(TemplateCatalog? catalog)
    {
        var items = new List<Usage>();
        if (catalog is null)
            return items;

        Trace.Log($"current directory {Environment.CurrentDirectory}");

        var project = ModProject.TryFind(Environment.CurrentDirectory);
        if (project is null)
            return items;

        var root = Path.GetDirectoryName(project.ManifestPath)!;
        Trace.Log($"project root {root}");

        var usage = ApiUsage.Scan(root, catalog);

        //Problems first then by name, so what needs fixing is visible at a glance on the panel
        var ordered = usage.Items
            .OrderBy(item => item.State == UsageState.Ok ? 1 : 0)
            .ThenBy(item => item.Symbol, StringComparer.Ordinal);

        foreach (var item in ordered)
        {
            items.Add(new Usage(
                item.Symbol,
                item.State.ToString().ToLowerInvariant(),
                item.Type,
                item.Member,
                item.Candidates,
                item.Locations));
        }

        Console.WriteLine($"Template panel scanned {items.Count} api usage(s)");
        return items;
    }

    //Loads the catalog's document, turning any failure into a markdown explanation
    private static string LoadDocument(TemplateCatalog? catalog)
    {
        if (catalog is null)
            return "# No catalog available\n\nThe catalog could not be loaded. Check the network, or the local `Template` cache directory.";

        if (string.IsNullOrWhiteSpace(catalog.Document))
            return "# The catalog declares no document\n\nAdd a `document` field to `NacreCraftTemplate.yaml`.";

        var url = catalog.ResolveUrl(catalog.Document);
        var path = TemplateStore.FetchFile(url, catalog.Document, out var error);
        if (path is null)
            return $"# The document could not be fetched\n\n`{url}`\n\n```\n{error}\n```";

        return File.ReadAllText(path);
    }

    private static string Resource(string name)
    {
        using var stream = typeof(UiPage).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource {name} is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Payload(string Document, List<Usage> Usage);

    private sealed record Usage(
        string Symbol,
        string State,
        string Type,
        string Member,
        List<string> Candidates,
        List<UsageLocation> Locations);
}
