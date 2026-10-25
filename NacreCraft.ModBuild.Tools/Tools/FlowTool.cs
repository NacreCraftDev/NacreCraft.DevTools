using NacreCraft.ModBuild.Compile;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;
using Spectre.Console;

namespace NacreCraft.ModBuild.Tools;

//FlowTool prints the execution order of a class as a tree, so the control flow is readable without C#
internal static class FlowTool
{
    public static void Register()
        => ToolRegistry.Register("flow", "Show the execution order of a class as a structured outline", Run,
        [
            new("-p, --project <dir>", "Project directory to inspect, defaults to the current directory"),
            new("-f, --file <path>", "Source file to inspect, repeatable, defaults to every project source"),
            new("-c, --class <name>", "Class to show, defaults to every class in the selected files"),
            new("--no-ncproj", "Ignore the ncproj config, scan every source file and allow a plain directory"),
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
        var files = new List<string>();
        var excludes = new List<string>();
        var noNcproj = false;
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
                    files.Add(file);
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
                case "--no-ncproj":
                    noNcproj = true;
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
                        Console.WriteLine($"error: unknown flow option {args[index]}");
                        Usage();
                        return 1;
                    }

                    //a positional *.cs is a file, anything else names the class to show
                    if (args[index].EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        files.Add(args[index]);
                    else if (className is null)
                        className = args[index];
                    else
                    {
                        Console.WriteLine($"error: unexpected argument {args[index]}");
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

        SourceFiles.Configure(config, excludes, ignoreConfig: noNcproj);

        if (config is not null && !BuildTool.Prepare(rootDirectory, config))
            return 1;

        var options = config is null
            ? CompileOptions.Default
            : CompileOptions.From(config, config.AssemblyNameOf(rootDirectory));

        ProjectCompilation? project = null;
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
                });
        }
        else
        {
            project = CompilationFactory.Create(rootDirectory, options, out failure);
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
            foreach (var knownPath in selected)
            {
                if (!known.Contains(knownPath))
                {
                    Error($"file {knownPath} is not part of the project");
                    return 1;
                }
            }

            sources = project.Sources
                .Where(path => selected.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var color = output is null
            && !Console.IsOutputRedirected
            && !noColor
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        var text = FlowRenderer.Render(project.Compilation, sources, rootDirectory, className, all, depth, color,
            out var error);
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
        var parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        File.WriteAllText(outputPath, text);
        Console.WriteLine($"wrote {outputPath}");
        return 0;
    }

    private static void Usage()
        => Console.WriteLine(
            "Usage: ncm flow [-p|--project <dir>] [-f|--file <path>] [-c|--class <name>] [--no-ncproj] [--exclude <pattern>] [--depth <n>] [--all] [--no-color] [-o|--output <file>]");

    //Update moves a progress task to the file that just finished
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
