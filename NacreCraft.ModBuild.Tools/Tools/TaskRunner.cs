using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using NacreCraft.ModBuild.Core;

//System.Diagnostics also has a Trace, so alias the project's own here
using Trace = NacreCraft.ModBuild.Core.Trace;

namespace NacreCraft.ModBuild.Tools;

//TaskRunner runs the tasks defined in the project config
//It orders the dependencies first, then runs them one by one and stops at the first failing command
internal static class TaskRunner
{
    //Variable matches the $(Name) and $(env:Name) forms in a command line
    private static readonly Regex Variable = new(@"\$\((env:)?([A-Za-z_][A-Za-z0-9_]*)\)", RegexOptions.Compiled);

    //Run runs a task together with its dependencies and returns the process exit code
    //root overrides where the steps run and what $(ProjectDir) expands to; a global task passes the current directory,
    //since it belongs to no project and is expected to act on whatever the command was issued in
    public static int Run(NcProject project, NcTask task, string? root = null)
    {
        var directory = root ?? project.Directory;
        var order = Order(project, task, out var error);
        if (order is null)
        {
            Console.WriteLine($"error: {error}");
            return 1;
        }

        //A task with Override can replace a built-in tool, which lets a cloned project run arbitrary commands
        //Only a name that really belongs to a built-in asks first; the project wide switch alone changes nothing
        foreach (var current in order)
        {
            if (current.Overrides && NcProject.IsBuiltInName(current.Name) && !Approved(project, current))
                return 1;
        }

        var variables = Variables(project, directory);
        foreach (var current in order)
        {
            if (current.Steps.Count == 0)
                continue;

            Console.WriteLine($"Running task {current.Name}");
            foreach (var step in current.Steps)
            {
                if (!Step(step, variables, directory, project))
                    return 1;
            }
        }
        return 0;
    }

    //Step runs one step, unknown kinds are already rejected during parsing
    private static bool Step(NcStep step, IReadOnlyDictionary<string, string> variables, string root, NcProject project)
    {
        if (step.Kind == NcStepKind.Exec)
        {
            var line = Interpolate(step.Command, variables);
            Console.WriteLine($"> {line}");
            return Execute(line, root);
        }

        if (step.Kind == NcStepKind.Tool)
        {
            var plugin = Interpolate(step.Name, variables);
            var method = Interpolate(step.Method, variables);
            var args = Interpolate(step.Args, variables);
            Console.WriteLine($"> tool {plugin} {method} {args}".TrimEnd());
            return Tool(plugin, method, args);
        }

        if (step.Kind == NcStepKind.Script)
        {
            Console.WriteLine($"> {step.Text}");
            return ScriptRunner.Run(step, project, variables, root, project.AllowScriptClr);
        }

        var from = Interpolate(step.From, variables);
        var to = Interpolate(step.To, variables);
        Console.WriteLine($"> {(step.Kind == NcStepKind.Copy ? "copy" : "zip")} {from} -> {to}");
        return step.Kind == NcStepKind.Copy ? Copy(root, from, to) : Zip(root, from, to);
    }

    //Copy copies the matched files to the target
    //To counts as a directory when it ends with a separator, matches several files or already exists, otherwise as a single file
    //Directory mode keeps the structure below the pattern base so same-named files in subfolders do not overwrite each other
    private static bool Copy(string root, string from, string to)
    {
        var files = PathPattern.Match(root, from).ToList();
        if (files.Count == 0)
        {
            Console.WriteLine($"warning: {from} matched no file, nothing is copied");
            return true;
        }

        var target = Path.GetFullPath(Path.Combine(root, to));
        var asDirectory = EndsWithSeparator(to) || files.Count > 1 || Directory.Exists(target);
        var source = BaseOf(root, from);

        var copied = 0;
        foreach (var file in files)
        {
            var destination = asDirectory ? Path.Combine(target, Path.GetRelativePath(source, file)) : target;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                copied++;
            }
            catch (IOException e)
            {
                Console.WriteLine($"error: cannot copy {file} to {destination}: {e.Message}");
                return false;
            }
        }

        Console.WriteLine($"Copied {copied} file(s) to {target}");
        return true;
    }

    //Zip packs the whole source directory into a zip
    //The target file itself is skipped, since it is often placed inside the source directory and the previous one would be included
    private static bool Zip(string root, string from, string to)
    {
        var source = Path.GetFullPath(Path.Combine(root, from));
        if (!Directory.Exists(source))
        {
            Console.WriteLine($"error: {from} is not a directory, nothing is packed");
            return false;
        }

        var target = Path.GetFullPath(Path.Combine(root, to));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
                File.Delete(target);

            using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
            var entries = 0;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), target, StringComparison.OrdinalIgnoreCase))
                    continue;

                archive.CreateEntryFromFile(file, Path.GetRelativePath(source, file).Replace('\\', '/'));
                entries++;
            }

            Console.WriteLine($"Packed {entries} file(s) into {target}");
            return true;
        }
        catch (IOException e)
        {
            Console.WriteLine($"error: cannot write {target}: {e.Message}");
            return false;
        }
    }

    //BaseOf is the part of the pattern before the first wildcard, matching how PathPattern picks its base
    //It lets matched files be expressed relative to that base
    private static string BaseOf(string root, string from)
    {
        var normalized = from.Replace('\\', '/').TrimStart('/');
        var wildcard = normalized.IndexOfAny(['*', '?']);
        if (wildcard < 0)
        {
            var absolute = Path.GetFullPath(Path.Combine(root, normalized));
            return Directory.Exists(absolute) ? absolute : Path.GetDirectoryName(absolute)!;
        }

        var slash = normalized.LastIndexOf('/', wildcard);
        return slash <= 0 ? root : Path.GetFullPath(Path.Combine(root, normalized[..slash]));
    }

    //EndsWithSeparator tells whether the value denotes a directory
    private static bool EndsWithSeparator(string value)
        => value.EndsWith('/') || value.EndsWith('\\');

    //Order sorts the tasks into a chain by Depends and fails on a cycle or a missing dependency
    private static List<NcTask>? Order(NcProject project, NcTask task, out string error)
    {
        var ordered = new List<NcTask>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var failure = string.Empty;

        if (Visit(task))
        {
            error = string.Empty;
            return ordered;
        }

        error = failure;
        return null;

        bool Visit(NcTask current)
        {
            if (done.Contains(current.Name))
                return true;

            if (!visiting.Add(current.Name))
            {
                failure = $"task dependency cycle at '{current.Name}'";
                return false;
            }

            foreach (var name in current.Depends)
            {
                var next = project.FindTask(name);
                if (next is null)
                {
                    failure = $"task '{current.Name}' depends on '{name}', which is not defined";
                    return false;
                }

                if (!Visit(next))
                    return false;
            }

            visiting.Remove(current.Name);
            done.Add(current.Name);
            ordered.Add(current);
            return true;
        }
    }

    //Variables are the values that can be interpolated into a command line
    //The mod values come from the manifest and must not be redefined in the project, since a mismatch is hard to trace
    public static Dictionary<string, string> Variables(NcProject project, string root)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ProjectDir"] = root,
            ["Configuration"] = project.Build.Configuration,
        };

        var manifest = ModProject.TryFind(root);
        if (manifest is not null)
        {
            values["ModId"] = manifest.Id;
            values["ModName"] = manifest.DisplayName;
            values["ModVersion"] = manifest.Version;
        }
        return values;
    }

    //Interpolate replaces $(Name) with its value and leaves unknown names as is instead of silently dropping text
    private static string Interpolate(string text, IReadOnlyDictionary<string, string> variables)
        => Variable.Replace(text, match =>
        {
            var name = match.Groups[2].Value;
            if (match.Groups[1].Success)
                return Environment.GetEnvironmentVariable(name) ?? string.Empty;

            if (variables.TryGetValue(name, out var value))
                return value;

            Trace.Log($"unknown variable {name} in \"{text}\", left as is");
            return match.Value;
        });

    //Approved only lets an overriding task run when its content was confirmed once
    //A changed command asks again, so an approval cannot be swapped out afterwards
    private static bool Approved(NcProject project, NcTask task)
    {
        if (TaskApprovals.IsApproved(project, task))
            return true;

        //When there is no way to ask it refuses, since such a command in an unknown repo should not run unattended
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.WriteLine(
                $"error: task {task.Name} overrides a built-in tool, run it once in an interactive terminal to confirm");
            return false;
        }

        Console.WriteLine($"Task {task.Name} overrides the built-in {task.Name} tool:");
        foreach (var step in task.Steps)
            Console.WriteLine($"  {step.Text}");

        Console.Write("Run it? [y/N] ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Aborted");
            return false;
        }

        TaskApprovals.Approve(project, task);
        return true;
    }

    //Tool calls an installed plugin inside this process, the arguments split the same way the server arguments are
    private static bool Tool(string plugin, string method, string args)
    {
        var code = PluginHost.Run(plugin, method, ArgumentLine.Split(args), out var error);
        if (code == 0)
            return true;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(!string.IsNullOrEmpty(error)
            ? $"error: {error}"
            : $"error: plugin {plugin} {method} exited with code {code}");
        Console.ResetColor();
        return false;
    }

    //Execute runs a command through the system shell with the project root as the working directory
    private static bool Execute(string command, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };

        if (OperatingSystem.IsWindows())
        {
            //cmd has its own quoting rules, so the whole line goes to it rather than through .NET escaping
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/c " + command;
        }
        else
        {
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.WriteLine("error: could not start the shell");
            return false;
        }

        process.WaitForExit();
        if (process.ExitCode == 0)
            return true;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"error: command exited with code {process.ExitCode}");
        Console.ResetColor();
        return false;
    }
}
