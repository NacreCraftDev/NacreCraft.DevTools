using System.Text;
using NacreCraft.Pit.Core;
using NacreCraft.Pit.Experimental.Java;
using NacreCraft.Pit.Tools;

namespace NacreCraft.Pit.Experimental.Commands;

//JavaCallGraphCommand prints the call tree of a Java source tree
//It takes the same options as the shipped callgraph and differs in two ways: IronJava parses the sources and the
//report stops at the tree, because this tool has no cycle check
internal static class JavaCallGraphCommand
{
    //zero means a tree is expanded until every branch ends
    private const int NoLimit = 0;

    public static void Register()
        => ToolRegistry.Register("java-callgraph", "Inspect the call graph of a Java source tree", Run,
        [
            new("-p, --project <dir>", "Directory holding the Java sources, defaults to the current directory"),
            new("-r, --root <method>", "Start from this method instead of the detected entries, repeatable"),
            new("--exclude <pattern>", "Skip source paths matching this pattern, repeatable"),
            new("--depth <n>", "Limit the call depth, 0 means no limit"),
            new("--no-external", "Hide calls that leave the project sources"),
            new("--no-color", "Disable ansi colours in the console"),
            new("-o, --output <file>", "Write the report to a file instead of the console"),
        ]);

    private static int Run(string[] args)
    {
        string? directory = null;
        string? output = null;
        var roots = new List<string>();
        var excludes = new List<string>();
        var noExternal = false;
        var noColor = false;
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
                case "--no-external":
                    noExternal = true;
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
                    Error($"unknown java-callgraph option {args[index]}");
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

        var patterns = excludes.Select(PathPattern.Compile).ToList();
        var files = Directory.EnumerateFiles(start, "*.java", SearchOption.AllDirectories)
            .Where(path => !patterns.Any(match => match(Path.GetRelativePath(start, path).Replace('\\', '/'))))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            Error($"no java source below {start}");
            return 1;
        }

        JavaCallGraph? graph = null;
        var skipped = 0;
        ProgressLine.Run(false, () =>
        {
            ProgressLine.SetTotal(2);
            ProgressLine.Enter("parse", files.Count, "java sources");
            graph = JavaGraphBuilder.Build(start, files, ProgressLine.Step, ProgressLine.Detail, out skipped);
            ProgressLine.Enter("collect", 1, "resolving calls");
        });

        if (graph is null)
        {
            Error("the sources could not be parsed");
            return 1;
        }

        //the parser rejects a few valid constructs, so the count is reported rather than silently dropped
        if (skipped > 0)
            Console.WriteLine($"note: {skipped} java file(s) could not be parsed and were skipped");

        var entries = roots.Count > 0 ? Resolve(graph, roots) : Detect(graph);
        if (entries is null)
            return 1;

        if (entries.Count == 0)
        {
            Error("no entry method found, name one with --root <method>");
            return 1;
        }

        var color = output is null
            && !Console.IsOutputRedirected
            && !noColor
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        var text = JavaGraphRender.Render(graph, entries, start, depth, !noExternal, color);
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

    //a java tree has thousands of public methods nothing in the tree calls, so main is the entry when one exists
    private const int MaxEntries = 200;

    //Detect chooses where the report starts: main when the tree declares one, otherwise the methods nothing calls
    //constructors are reached from their callers and are never entries
    private static List<JavaCallGraph.Method> Detect(JavaCallGraph graph)
    {
        var methods = graph.Local
            .Where(method => method.Incoming == 0 && !method.IsPrivate && method.Name != "<init>")
            .ToList();

        //a declared main is the program entry, and listing every unreferenced method beside it would bury it
        var mains = methods.Where(IsMain).ToList();
        if (mains.Count > 0)
            return mains.OrderBy(method => method.Signature, StringComparer.Ordinal).ToList();

        //without a main the tree is a library, so the unreferenced methods stand in, capped to keep the report readable
        return methods
            .OrderBy(method => method.Signature, StringComparer.Ordinal)
            .Take(MaxEntries)
            .ToList();
    }

    private static bool IsMain(JavaCallGraph.Method method)
        => method.Name == "main" && method.IsStatic;

    //Resolve turns --root names into the methods they name, reporting every ambiguity instead of guessing
    private static List<JavaCallGraph.Method>? Resolve(JavaCallGraph graph, IReadOnlyList<string> queries)
    {
        var methods = graph.Local.ToList();
        var entries = new List<JavaCallGraph.Method>();

        foreach (var query in queries)
        {
            var matches = methods.Where(method => Matches(method, query)).ToList();
            if (matches.Count == 0)
            {
                Error($"no method matches {query}");
                return null;
            }

            if (matches.Count > 1)
            {
                Error($"{query} matches {matches.Count} methods, write Type.Method");
                foreach (var match in matches.Take(8))
                    Console.WriteLine($"  {match.Type}.{match.Name}");
                return null;
            }

            entries.Add(matches[0]);
        }

        return entries;
    }

    private static bool Matches(JavaCallGraph.Method method, string query)
        => method.Name.Equals(query, StringComparison.OrdinalIgnoreCase)
            || method.Signature.Equals(query, StringComparison.OrdinalIgnoreCase)
            || $"{method.Type}.{method.Name}".Equals(query, StringComparison.OrdinalIgnoreCase);

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

    private static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: {message}");
        Console.ResetColor();
    }

    private static void Usage()
        => Console.WriteLine(
            "Usage: pit-exp java-callgraph [-p|--project <dir>] [-r|--root <method>] [--exclude <pattern>] [--depth <n>] [--no-external] [--no-color] [-o|--output <file>]");
}
