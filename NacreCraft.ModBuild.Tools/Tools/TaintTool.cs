using NacreCraft.ModBuild.Compile;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;
using Spectre.Console;

namespace NacreCraft.ModBuild.Tools;

//TaintTool follows untrusted values from a root method to a sink and prints the route
internal static class TaintTool
{
    private const string Reset = "\u001b[0m";
    private const string HeaderColor = "\u001b[36m";
    private const string ChainColor = "\u001b[33m";
    private const string SinkColor = "\u001b[31m";
    private const string DimColor = "\u001b[90m";

    public static void Register()
        => ToolRegistry.Register("taint", "Follow untrusted values from a root method to a sink", Run,
        [
            new("-p, --project <dir>", "Project directory to inspect, defaults to the current directory"),
            new("-f, --file <path>", "Source file to inspect, repeatable, defaults to every project source"),
            new("-c, --class <name>", "Class to inspect, repeatable"),
            new("-m, --method <name>", "Method to inspect, repeatable"),
            new("--no-ncproj", "Ignore the ncproj config, scan every source file and allow a plain directory"),
            new("--exclude <pattern>", "Skip source paths matching this pattern, repeatable"),
            new("-r, --root <method>", "Root method whose parameters are untrusted, repeatable, defaults to every method"),
            new("--root-parameter <name>", "Only treat this parameter of a root as untrusted, repeatable, all when omitted"),
            new("--source <pattern>", "Treat a matching method as a source, repeatable"),
            new("--sink <pattern>", "Treat a matching method as a sink, repeatable"),
            new("--sanitizer <pattern>", "Treat a matching method as a sanitizer, repeatable"),
            new("--rules <file>", "Json file with sources, sinks and sanitizers arrays"),
            new("--depth <n>", "Interprocedural depth, defaults to 8, 0 means the internal limit"),
            new("--interprocedural <mode>", "none or summary, defaults to summary"),
            new("--no-fields", "Do not follow fields and properties"),
            new("--no-external", "Stop propagation at the project boundary instead of following calls out of it"),
            new("--max-paths <n>", "Stop after this many paths, defaults to 200"),
            new("--no-color", "Disable ansi colours in the console"),
            new("-o, --output <file>", "Write the report to a file instead of the console"),
        ]);

    private static int Run(string[] args)
    {
        string? directory = null;
        string? output = null;
        string? rulesPath = null;
        var roots = new List<string>();
        var rootParameters = new List<string>();
        var classes = new List<string>();
        var methods = new List<string>();
        var files = new List<string>();
        var excludes = new List<string>();
        var rules = new List<TaintRule>();
        var noNcproj = false;
        var interprocedural = true;
        var fields = true;
        var external = true;
        var noColor = false;
        var depth = 8;
        var maxPaths = 200;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-p":
                case "--project":
                    if (!Value(args, ref index, out directory))
                        return 1;
                    break;
                case "-f":
                case "--file":
                    if (!Value(args, ref index, out var file))
                        return 1;
                    files.Add(file);
                    break;
                case "-c":
                case "--class":
                    if (!Value(args, ref index, out var className))
                        return 1;
                    classes.Add(className);
                    break;
                case "-m":
                case "--method":
                    if (!Value(args, ref index, out var methodName))
                        return 1;
                    methods.Add(methodName);
                    break;
                case "-r":
                case "--root":
                    if (!Value(args, ref index, out var root))
                        return 1;
                    roots.Add(root);
                    break;
                case "--root-parameter":
                    if (!Value(args, ref index, out var rootParameter))
                        return 1;
                    rootParameters.Add(rootParameter);
                    break;
                case "--source":
                    if (!Value(args, ref index, out var source))
                        return 1;
                    rules.Add(new TaintRule(TaintKind.Source, source));
                    break;
                case "--sink":
                    if (!Value(args, ref index, out var sink))
                        return 1;
                    rules.Add(new TaintRule(TaintKind.Sink, sink));
                    break;
                case "--sanitizer":
                    if (!Value(args, ref index, out var sanitizer))
                        return 1;
                    rules.Add(new TaintRule(TaintKind.Sanitizer, sanitizer));
                    break;
                case "--rules":
                    if (!Value(args, ref index, out rulesPath))
                        return 1;
                    break;
                case "--exclude":
                    if (!Value(args, ref index, out var exclude))
                        return 1;
                    excludes.Add(exclude);
                    break;
                case "--depth":
                    if (!Number(args, ref index, out depth))
                        return 1;
                    break;
                case "--interprocedural":
                    if (!Value(args, ref index, out var mode))
                        return 1;

                    if (mode.Equals("none", StringComparison.OrdinalIgnoreCase))
                        interprocedural = false;
                    else if (mode.Equals("summary", StringComparison.OrdinalIgnoreCase))
                        interprocedural = true;
                    else
                    {
                        Error("--interprocedural supports none or summary");
                        return 1;
                    }
                    break;
                case "--no-fields":
                    fields = false;
                    break;
                case "--no-external":
                    external = false;
                    break;
                case "--max-paths":
                    if (!Number(args, ref index, out maxPaths) || maxPaths <= 0)
                    {
                        Error("--max-paths needs a positive number");
                        return 1;
                    }
                    break;
                case "--no-ncproj":
                    noNcproj = true;
                    break;
                case "--no-color":
                    noColor = true;
                    break;
                case "-o":
                case "--output":
                    if (!Value(args, ref index, out output))
                        return 1;
                    break;
                default:
                    Console.WriteLine($"error: unknown taint option {args[index]}");
                    Usage();
                    return 1;
            }
        }

        if (rulesPath is not null)
        {
            try
            {
                rules.AddRange(TaintMatcher.Load(Path.GetFullPath(rulesPath)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or Newtonsoft.Json.JsonException)
            {
                Error($"cannot read rules {rulesPath}: {exception.Message}");
                return 1;
            }
        }

        var start = Path.GetFullPath(directory ?? Environment.CurrentDirectory);
        if (!Directory.Exists(start))
        {
            Error($"no directory {start}");
            return 1;
        }

        NcProject? config = null;
        var rootDirectory = start;
        if (!noNcproj)
        {
            config = NcProject.TryFind(start, out var configError);
            if (!string.IsNullOrEmpty(configError))
            {
                Error(configError);
                return 1;
            }

            if (config is null)
            {
                Error($"no {NcProject.Extension} in this directory or any parent, use --no-ncproj for a plain C# project");
                return 1;
            }

            if (config.Bootstrap)
            {
                Error("a bootstrap config has no sources of its own");
                return 1;
            }

            rootDirectory = config.Directory;
        }

        SourceFiles.Configure(config, excludes, ignoreConfig: noNcproj);
        if (config is not null && !BuildTool.Prepare(rootDirectory, config))
            return 1;

        var compileOptions = config is null
            ? CompileOptions.Default
            : CompileOptions.From(config, config.AssemblyNameOf(rootDirectory));

        ProjectCompilation? project = null;
        var failure = string.Empty;
        if (!Console.IsOutputRedirected && !Console.IsErrorRedirected)
        {
            AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new ScanColumn())
                .Start(context =>
                {
                    var compile = context.AddTask("scanning", maxValue: 100);
                    project = CompilationFactory.Create(rootDirectory, compileOptions, out failure,
                        (done, total, file) => Update(compile, done, total, file, rootDirectory));
                    context.RemoveTask(compile);
                });
        }
        else
        {
            project = CompilationFactory.Create(rootDirectory, compileOptions, out failure);
        }

        if (project is null)
        {
            Error(failure.Length > 0 ? failure : "the project could not be scanned");
            return 1;
        }

        IReadOnlyCollection<string> sources = project.Sources;
        if (files.Count > 0)
        {
            var selected = new List<string>();
            foreach (var file in files)
            {
                var filePath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, file));
                if (!File.Exists(filePath))
                {
                    Error($"no file {filePath}");
                    return 1;
                }

                selected.Add(filePath);
            }

            var known = new HashSet<string>(project.Sources.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            foreach (var selectedPath in selected)
            {
                if (!known.Contains(selectedPath))
                {
                    Error($"file {selectedPath} is not part of the project");
                    return 1;
                }
            }

            sources = project.Sources
                .Where(path => selected.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var options = new TaintOptions(roots, classes, methods, rules, depth, interprocedural, fields, external,
            maxPaths, true, rootParameters);
        var paths = TaintAnalysis.Analyze(project.Compilation, sources, options, out var error);
        if (error.Length > 0)
        {
            Error(error);
            return 1;
        }

        var color = output is null
            && !Console.IsOutputRedirected
            && !noColor
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        var text = Render(paths, rootDirectory, color);

        if (output is null)
        {
            Console.Write(text);
            return 0;
        }

        var outputPath = Path.GetFullPath(output);
        var parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        File.WriteAllText(outputPath, text);
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    private static string Render(IReadOnlyList<TaintPath> paths, string rootDirectory, bool color)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(Paint($"taint for {rootDirectory}", HeaderColor, color));
        if (paths.Count == 0)
        {
            text.AppendLine();
            text.AppendLine(Paint("  no taint path found", DimColor, color));
            return text.ToString();
        }

        foreach (var path in paths)
        {
            text.AppendLine();
            text.AppendLine($"  {Paint(path.Chain, ChainColor, color)}");
            var span = path.Sink.GetLineSpan();
            var file = Path.GetRelativePath(rootDirectory, span.Path).Replace('\\', '/');
            var location = $"{file}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}";
            text.AppendLine($"    {Paint("sink", SinkColor, color)} {Paint(location, DimColor, color)}  {path.Text}");
        }

        return text.ToString();
    }

    private static string Paint(string text, string color, bool enabled)
        => enabled ? color + text + Reset : text;

    private static void Usage()
        => Console.WriteLine(
            "Usage: ncm taint [-p|--project <dir>] [-f|--file <path>] [-c|--class <name>] [-m|--method <name>] [-r|--root <method>] [--root-parameter <name>] [--source <pattern>] [--sink <pattern>] [--sanitizer <pattern>] [--rules <file>] [--depth <n>] [--interprocedural none|summary] [--no-fields] [--no-external] [--max-paths <n>] [--no-ncproj] [--exclude <pattern>] [--no-color] [-o|--output <file>]");

    private static void Update(ProgressTask task, int done, int total, string file, string rootDirectory)
    {
        task.MaxValue = Math.Max(1, total);
        task.Value = done;
        task.Description = file.Length == 0
            ? string.Empty
            : Path.GetRelativePath(rootDirectory, file).Replace('\\', '/');
    }

    private static bool Value(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            Console.WriteLine($"error: {args[index]} needs a value");
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool Number(string[] args, ref int index, out int value)
    {
        var option = args[index];
        if (!Value(args, ref index, out var text))
        {
            value = 0;
            return false;
        }

        if (int.TryParse(text, out value) && value >= 0)
            return true;

        Console.WriteLine($"error: {option} needs a non-negative number");
        value = 0;
        return false;
    }

    private static void Error(string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: {message}");
        Console.ForegroundColor = previous;
    }
}
