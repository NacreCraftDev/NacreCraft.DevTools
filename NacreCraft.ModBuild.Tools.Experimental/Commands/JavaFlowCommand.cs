using System.Text;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Tools.Experimental.Java;

namespace NacreCraft.ModBuild.Tools.Experimental.Commands;

//JavaFlowCommand prints the execution order of a Java class as a structured outline
//It takes the same options as the shipped flow, minus the ncproj switch, since a Java tree has no project config
internal static class JavaFlowCommand
{
    public static void Register()
        => ToolRegistry.Register("java-flow", "Show the execution order of a Java class as a structured outline", Run,
        [
            new("-p, --project <dir>", "Directory holding the Java sources, defaults to the current directory"),
            new("-f, --file <path>", "Source file to inspect, repeatable, defaults to every java file"),
            new("-c, --class <name>", "Class to show, defaults to every class in the selected files"),
            new("--exclude <pattern>", "Skip source paths matching this pattern, repeatable"),
            new("--depth <n>", "Limit nested statement depth, 0 means no limit"),
            new("--all", "Expand every class body, not only the class named with --class"),
            new("--no-color", "Disable ansi colours in the console"),
            new("-o, --output <file>", "Write the outline to a file instead of the console"),
        ]);

    private static int Run(string[] args)
    {
        string? directory = null;
        string? className = null;
        string? output = null;
        var selectedFiles = new List<string>();
        var excludes = new List<string>();
        var all = false;
        var noColor = false;
        var depth = 0;

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
                    selectedFiles.Add(file);
                    break;
                case "-c":
                case "--class":
                    if (!Value(args, ref index, out className))
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
                case "--all":
                    all = true;
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
                    if (args[index].StartsWith('-'))
                    {
                        Error($"unknown java-flow option {args[index]}");
                        Usage();
                        return 1;
                    }

                    //a positional *.java is a file, anything else names the class to show
                    if (args[index].EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                        selectedFiles.Add(args[index]);
                    else if (className is null)
                        className = args[index];
                    else
                    {
                        Error($"unexpected argument {args[index]}");
                        Usage();
                        return 1;
                    }
                    break;
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

        if (selectedFiles.Count > 0)
        {
            var chosen = new List<string>();
            foreach (var file in selectedFiles)
            {
                var path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, file));
                if (!File.Exists(path))
                {
                    Error($"no file {path}");
                    return 1;
                }

                if (!files.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    Error($"file {path} is not among the scanned java sources");
                    return 1;
                }

                chosen.Add(path);
            }

            files = chosen;
        }

        if (files.Count == 0)
        {
            Error($"no java source below {start}");
            return 1;
        }

        List<JavaTypeFlow>? types = null;
        var skipped = 0;
        ProgressLine.Run(false, () =>
        {
            ProgressLine.SetTotal(2);
            ProgressLine.Enter("parse", files.Count, "java sources");
            types = JavaFlowSource.Read(files, start, className, all, ProgressLine.Step, out skipped);
            ProgressLine.Enter("outline", 1, "building flow");
        });

        if (types is null)
        {
            Error("the sources could not be parsed");
            return 1;
        }

        //the parser rejects a few valid constructs, so the count is reported rather than silently dropped
        if (skipped > 0)
            Console.WriteLine($"note: {skipped} java file(s) could not be parsed and were skipped");

        var color = output is null
            && !Console.IsOutputRedirected
            && !noColor
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        var text = JavaFlowRender.Render(types, start, className, all, depth, color, out var error);
        if (error.Length > 0)
        {
            Error(error);
            return 1;
        }

        if (output is null)
        {
            Console.Write(text);
            return 0;
        }

        var outputPath = Path.GetFullPath(output);
        var outputParent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputParent))
            Directory.CreateDirectory(outputParent);

        File.WriteAllText(outputPath, text, new UTF8Encoding(false));
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    private static bool Value(string[] args, ref int index, out string value)
    {
        value = string.Empty;
        if (index + 1 >= args.Length)
        {
            Error($"{args[index]} needs a value");
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

        Error($"{option} needs a non-negative number");
        value = 0;
        return false;
    }

    private static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: {message}");
        Console.ResetColor();
    }

    private static void Usage()
        => Console.WriteLine(
            "Usage: ncm-exp java-flow [-p|--project <dir>] [-f|--file <path>] [-c|--class <name>] [--exclude <pattern>] [--depth <n>] [--all] [--no-color] [-o|--output <file>]");
}
