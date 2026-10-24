using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Gui;
using NacreCraft.ModBuild.Tui;

namespace NacreCraft.ModBuild.Tools;

//TemplateTool browses and pulls mod development templates
//The catalog and example files are cached under Template in the program root, going online only when missing locally
internal static class TemplateTool
{
    public static void Register()
        => ToolRegistry.Register("template", "Browse and pull mod templates", Run,
        [
            new("view [pattern]", "List template entries, ? and * work as wildcards"),
            new("example <api id>", "Pull the example file of an entry into the current directory"),
            new("gui", "Open the template panel in a window"),
            new("tui", "Open the terminal panel, grading the api usage of the project"),
            new("--refresh", "Clear the local cache first and pull everything again"),
        ]);

    //RefreshOption clears the local cache and pulls everything again, accepted before or after the subcommand
    private const string RefreshOption = "--refresh";

    //Run picks the subcommand from the first non-option argument
    private static int Run(string[] args)
    {
        //The template source follows the current directory's project config, falling back to the built-in one
        TemplateStore.Configure(NcProject.TryFind(Environment.CurrentDirectory, out _));

        var refresh = false;
        var rest = new List<string>(args.Length);

        foreach (var arg in args)
        {
            if (arg == RefreshOption)
                refresh = true;
            else
                rest.Add(arg);
        }

        if (refresh)
        {
            //The download path needs to know this is a refresh, otherwise it would hit the stale CDN copy
            TemplateStore.Refresh = true;
            if (!ClearCache())
                return 1;
        }

        var remaining = rest.ToArray();
        if (remaining.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return remaining[0] switch
        {
            "view" => View(remaining[1..]),
            "example" => Example(remaining[1..]),
            "gui" => Gui(),
            "tui" => Tui(),
            _ => Unknown(remaining[0]),
        };
    }

    //ClearCache removes the whole cache directory, after which the usual fetch-if-missing path runs
    private static bool ClearCache()
    {
        var root = TemplateStore.Root;
        if (!Directory.Exists(root))
        {
            Console.WriteLine($"Template cache is already empty: {root}");
            return true;
        }

        try
        {
            Directory.Delete(root, recursive: true);
            Console.WriteLine($"Cleared template cache: {root}");
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"error: could not clear {root}: {e.Message}");
            return false;
        }
    }

    //View lists entries matching the pattern, or all of them without one
    private static int View(string[] args)
    {
        var catalog = TemplateStore.LoadCatalog();
        if (catalog is null)
            return 1;

        var pattern = args.Length > 0 ? args[0] : null;
        var matched = catalog.Apis.Where(entry => TemplateCatalog.Matches(entry.Id, pattern)).ToList();
        if (matched.Count == 0)
        {
            Console.WriteLine(string.IsNullOrWhiteSpace(pattern)
                ? "The template catalog has no entries"
                : $"No template entry matches {pattern}");
            return 1;
        }

        foreach (var entry in matched)
        {
            Console.WriteLine(entry.Id);
            if (!string.IsNullOrWhiteSpace(entry.Title))
                Console.WriteLine($"    {entry.Title}");
            if (!string.IsNullOrWhiteSpace(entry.Summary))
                Console.WriteLine($"    {entry.Summary}");
        }

        Console.WriteLine();
        Console.WriteLine($"{matched.Count} entry(s)");
        return 0;
    }

    //Example finds the entry by id and pulls its template file into the current directory
    private static int Example(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: ncm template example <api id>");
            return 1;
        }

        var catalog = TemplateStore.LoadCatalog();
        if (catalog is null)
            return 1;

        var id = args[0];
        var entry = catalog.Apis.FirstOrDefault(
            candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Console.WriteLine($"No template entry with id {id}, run 'ncm template view' to list them");
            return 1;
        }

        return PullExample(catalog, entry);
    }

    //PullExample pulls an entry's example into the current directory, shared by the example subcommand and the tui
    //A cache hit skips the network and an existing target asks before overwriting
    internal static int PullExample(TemplateCatalog catalog, TemplateEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Template))
        {
            Console.WriteLine($"{entry.Id} does not declare a template file");
            return 1;
        }

        var url = catalog.ResolveUrl(entry.Template);
        var source = TemplateStore.FetchFile(url, entry.Template, out var error);
        if (source is null)
        {
            Console.WriteLine($"Failed to fetch {url}: {error}");
            return 1;
        }

        var target = Path.Combine(Environment.CurrentDirectory, Path.GetFileName(entry.Template));
        if (File.Exists(target) && !ConfirmOverwrite(target))
            return 0;

        //Two placeholders are left in the template and filled from the current project, or from Example outside one
        var text = Fill(File.ReadAllText(source), ModProject.TryFind(Environment.CurrentDirectory), entry.Id);
        File.WriteAllText(target, text);

        Console.WriteLine($"Written to {target}");
        return 0;
    }

    //NamespaceToken and ClassToken are the two placeholders left in the template
    private const string NamespaceToken = "__MOD_NAMESPACE__";
    private const string ClassToken = "__MOD_CLASS__";

    //FallbackName is used for both names outside a mod project
    private const string FallbackName = "Example";

    //Fill replaces the template placeholders with the names of the current project
    //Inside a project the namespace comes from the entry manifest and the class is the api name plus Example
    //Outside one both fall back to Example
    private static string Fill(string text, ModProject? project, string apiId)
    {
        var inside = project is not null && !string.IsNullOrEmpty(project.Namespace);
        var namespaceName = inside ? project!.Namespace : FallbackName;
        var className = inside ? apiId.Split('.')[^1] + "Example" : FallbackName;

        return text
            .Replace(NamespaceToken, namespaceName)
            .Replace(ClassToken, className);
    }

    //Gui opens the graphical panel, which still opens without a catalog and explains why in the window
    private static int Gui()
    {
        var catalog = TemplateStore.LoadCatalog();
        TemplateWindow.Show(catalog);
        return 0;
    }

    //Tui opens the terminal panel, which focuses on scanning the project for api usage
    private static int Tui() => UsageTui.Run();

    //Unknown reports an unrecognized subcommand
    private static int Unknown(string command)
    {
        Console.WriteLine($"Unknown template command: {command}");
        PrintUsage();
        return 1;
    }

    //ConfirmOverwrite asks when the target exists, with an empty answer meaning no
    private static bool ConfirmOverwrite(string path)
    {
        Console.Write($"{Path.GetFileName(path)} already exists. Overwrite? (y/N) ");
        var answer = Console.ReadLine();
        return answer is not null && answer.StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    //PrintUsage lists the subcommands
    private static void PrintUsage()
    {
        Console.WriteLine("Usage: ncm template <command>");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  view [pattern]     List template entries, ? and * work as wildcards");
        Console.WriteLine("  example <api id>   Pull the example file of an entry into the current directory");
        Console.WriteLine("  gui                Open the template panel in a window");
        Console.WriteLine("  tui                Open the terminal panel, grading the api usage of the project");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --refresh          Clear the local cache first and pull everything again");
    }
}
