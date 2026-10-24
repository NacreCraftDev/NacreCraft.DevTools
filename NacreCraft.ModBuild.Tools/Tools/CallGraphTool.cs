using System.Text;
using Microsoft.CodeAnalysis;
using NacreCraft.ModBuild.Compile;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;
using Spectre.Console;

namespace NacreCraft.ModBuild.Tools;

//CallGraphTool prints the call tree of a project and reports the cycles that can take the runtime down
//It is the experimental counterpart of the build check: the same CallGraph walks the edges, but the output is a tree a
//person reads instead of a diagnostic
internal static class CallGraphTool
{
    //Zero means a tree is expanded until every branch ends
    private const int NoLimit = 0;

    public static void Register()
        => ToolRegistry.Register("callgraph", "Inspect the project call graph and report call cycles", Run,
        [
            new("-p, --project <dir>", "Project directory to inspect, defaults to the current directory or its ncproj parent"),
            new("-r, --root <method>", "Start from this method instead of the detected entries, repeatable"),
            new("--no-ncproj", "Ignore the ncproj config, scan every source file and allow a plain directory"),
            new("--exclude <pattern>", "Skip source paths matching this pattern, repeatable"),
            new("--depth <n>", "Limit tree depth, 0 means no limit"),
            new("--tree", "Draw a top down organisation tree instead of the indented list"),
            new("--no-external", "Hide calls that leave the project sources"),
            new("--no-cycles", "Skip the call cycle check"),
            new("--no-color", "Disable ansi colours in the console"),
            new("-o, --output <file>", "Write the report to a file instead of the console"),
        ]);

    private static int Run(string[] args)
    {
        string? directory = null;
        string? output = null;
        var roots = new List<string>();
        var excludes = new List<string>();
        var noNcproj = false;
        var noExternal = false;
        var noCycles = false;
        var noColor = false;
        var tree = false;
        var depth = NoLimit;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-p":
                case "--project":
                    if (!Value(args, ref index, out directory))
                        return 1;
                    break;
                case "-r":
                case "--root":
                    if (!Value(args, ref index, out var root))
                        return 1;
                    roots.Add(root);
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
                case "--no-ncproj":
                    noNcproj = true;
                    break;
                case "--no-external":
                    noExternal = true;
                    break;
                case "--no-cycles":
                    noCycles = true;
                    break;
                case "--tree":
                    tree = true;
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
                    Console.WriteLine($"error: unknown callgraph option {args[index]}");
                    Usage();
                    return 1;
            }
        }

        var start = Path.GetFullPath(directory ?? Environment.CurrentDirectory);
        if (!Directory.Exists(start))
        {
            Error($"no directory {start}");
            return 1;
        }

        //without the ncproj the directory itself is the project, otherwise the config marks the root
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

        //exclusions must be in place before anything enumerates, and --no-ncproj drops the patterns from the config
        SourceFiles.Configure(config, excludes, ignoreConfig: noNcproj);

        //the compile needs kernel assemblies, packages and project references, the same set a build resolves
        if (config is not null && !BuildTool.Prepare(rootDirectory, config))
            return 1;

        var options = config is null
            ? CompileOptions.Default
            : CompileOptions.From(config, config.AssemblyNameOf(rootDirectory));

        ProjectCompilation? project = null;
        CallGraph? graph = null;
        var failure = string.Empty;

        //the live bar needs a terminal; a redirected console gets the scan without it so logs stay clean
        if (!Console.IsOutputRedirected && !Console.IsErrorRedirected)
        {
            AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(new ScanColumn())
                .Start(context =>
                {
                    var compile = context.AddTask("scanning", maxValue: 100);
                    project = CompilationFactory.Create(rootDirectory, options, out failure,
                        (done, total, file) => Update(compile, done, total, file, rootDirectory));
                    context.RemoveTask(compile);
                    if (project is null)
                        return;

                    var collect = context.AddTask("scanning", maxValue: 100);
                    graph = CallGraphBuilder.Build(project.Compilation, project.Sources,
                        (done, total, file) => Update(collect, done, total, file, rootDirectory));
                });
        }
        else
        {
            project = CompilationFactory.Create(rootDirectory, options, out failure);
            if (project is not null)
                graph = CallGraphBuilder.Build(project.Compilation, project.Sources);
        }

        if (project is null || graph is null)
        {
            Error(failure.Length > 0 ? failure : "the project could not be scanned");
            return 1;
        }
        var entries = roots.Count > 0 ? Resolve(graph, roots, rootDirectory) : Detect(graph);
        if (entries is null)
            return 1;

        if (entries.Count == 0)
        {
            Error("no entry method found, name one with --root <method>");
            return 1;
        }

        //a file or a redirected console keeps plain text, and NO_COLOR turns the escapes off on request
        var color = output is null
            && !Console.IsOutputRedirected
            && !noColor
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        var text = CallGraphRender.Render(graph, entries, rootDirectory, depth, !noExternal, !noCycles, tree, color);
        if (output is null)
        {
            Console.Write(text);
            return 0;
        }

        var path = Path.GetFullPath(output);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        File.WriteAllText(path, text, new UTF8Encoding(false));
        Console.WriteLine($"Wrote {path}");
        return 0;
    }

    //Value reads the argument after an option
    private static bool Value(string[] args, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Length)
        {
            Error($"{args[index]} needs a value");
            return false;
        }

        value = args[index + 1];
        index++;
        return true;
    }

    //Number reads a non negative count after an option
    private static bool Number(string[] args, ref int index, out int value)
    {
        value = NoLimit;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out value) || value < 0)
        {
            Error($"{args[index]} needs a non negative number");
            return false;
        }

        index++;
        return true;
    }

    //Update moves a progress task to the file that just finished
    private static void Update(ProgressTask task, int done, int total, string file, string rootDirectory)
    {
        task.MaxValue = Math.Max(1, total);
        task.Value = done;
        task.Description = file.Length == 0
            ? string.Empty
            : Path.GetRelativePath(rootDirectory, file).Replace('\\', '/');
    }

    //Detect takes every method of the project nothing else calls, which is the top of the graph
    private static List<CallGraph.Node> Detect(CallGraph graph)
        => graph.Nodes()
            .Where(node => CallGraph.Local(node.Method) && node.Incoming == 0 && Candidate(node.Method))
            .OrderBy(node => IsMain(node.Method) ? 0 : 1)
            .ThenBy(node => CallGraph.Name(node.Method), StringComparer.Ordinal)
            .ToList();

    //Candidate whether a method can stand as an entry; constructors and local functions are reached from their callers
    private static bool Candidate(IMethodSymbol method)
        => method.MethodKind == MethodKind.Ordinary
            && (method.Name == "<Main>$" || (!method.IsImplicitlyDeclared && !method.Name.StartsWith('<')));

    private static bool IsMain(IMethodSymbol method) => method.Name is "Main" or "<Main>$";

    //Resolve turns --root names into the methods they name, reporting every ambiguity instead of guessing
    private static List<CallGraph.Node>? Resolve(CallGraph graph, IReadOnlyList<string> queries, string rootDirectory)
    {
        var nodes = graph.Nodes().Where(node => CallGraph.Local(node.Method)).ToList();
        var entries = new List<CallGraph.Node>();

        foreach (var query in queries)
        {
            var matches = nodes.Where(node => Matches(node.Method, query)).ToList();
            if (matches.Count == 0)
            {
                Error($"no method matches {query}");
                return null;
            }

            if (matches.Count > 1)
            {
                Error($"{query} matches {matches.Count} methods, write Type.Method");
                foreach (var match in matches.Take(8))
                    Console.WriteLine($"  {CallGraphRender.Label(match.Method, rootDirectory)}");
                return null;
            }

            entries.Add(matches[0]);
        }

        return entries;
    }

    //Matches a root name written as Method, Type.Method or the full signature
    private static bool Matches(IMethodSymbol method, string query)
    {
        var type = method.ContainingType?.Name ?? string.Empty;
        var written = type.Length == 0 ? method.Name : $"{type}.{method.Name}";

        if (method.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
            || written.Equals(query, StringComparison.OrdinalIgnoreCase))
            return true;

        var space = method.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var full = space.Length == 0 ? written : $"{space}.{written}";
        if (full.Equals(query, StringComparison.OrdinalIgnoreCase))
            return true;

        return query.Contains('(')
            && method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
                .Equals(query, StringComparison.OrdinalIgnoreCase);
    }

    private static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: {message}");
        Console.ResetColor();
    }

    private static void Usage()
        => Console.WriteLine(
            "Usage: ncm callgraph [-p|--project <dir>] [-r|--root <method>] [--no-ncproj] [--exclude <pattern>] [--depth <n>] [--tree] [--no-external] [--no-cycles] [--no-color] [-o|--output <file>]");
}
