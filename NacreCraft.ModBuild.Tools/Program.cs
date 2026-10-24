using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;
using NacreCraft.ModBuild.Tools;

namespace NacreCraft.ModBuild;

//Main entry point, the first argument is the tool name and a missing or unrecognized one prints help
public static class Program
{
    //Required by the windowing code, the window cannot even start on Windows without it
    [STAThread]
    public static int Main(string[] args)
    {
        //The engine comes from the local sdk, and the resolve event must be hooked before the first msbuild type loads
        MsBuildLibrary.Attach(out _);

        //Register a new tool in ToolRegistration, which both entry points share
        ToolRegistration.RegisterAll();

        //Machine wide defaults sit beside the caches and are created on first use
        //They fill in what a project leaves out and provide the tasks reachable from anywhere
        var global = GlobalConfig.Load();

        if (args.Length == 0)
        {
            Help.Print(Console.Out);
            return 0;
        }

        //`help <tool>` shows that tool's arguments, bare `help` shows the full list
        if (args[0] is "help" or "--help" or "-h")
        {
            if (args.Length > 1)
                return Help.PrintTool(Console.Out, args[1]);

            Help.Print(Console.Out);
            return 0;
        }

        //Project tasks are matched by exact name first and built-in tools only as a loose fallback
        //So a task named Build and the built-in build stay independent and both keep working
        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        return Dispatch(args[0], args[1..], project, global);
    }

    //Resolves work by name, with project tasks first, then the global ones and finally the built-in tools
    private static int Dispatch(string name, string[] rest, NcProject? project, NcProject? global)
    {
        //a bootstrap exposes the tasks of its projects as <bootstrap>/<task>, run in the owning project
        if (project is { Bootstrap: true } && project.FindScopedTask(name) is { } scoped)
        {
            if (rest.Length > 0)
                Console.WriteLine($"note: task arguments are not supported yet, {rest.Length} argument(s) ignored");

            return TaskRunner.Run(scoped.Project, scoped.Task);
        }

        var task = project?.FindTask(name);
        if (task is not null)
        {
            //Task arguments are not implemented yet, so they are accepted without error but reported as ignored
            if (rest.Length > 0)
                Console.WriteLine($"note: task arguments are not supported yet, {rest.Length} argument(s) ignored");

            return TaskRunner.Run(project!, task);
        }

        //A global task belongs to the machine rather than a project, so it runs wherever the command was issued
        var shared = global?.FindTask(name);
        if (shared is not null)
        {
            if (rest.Length > 0)
                Console.WriteLine($"note: task arguments are not supported yet, {rest.Length} argument(s) ignored");

            return TaskRunner.Run(global!, shared, Environment.CurrentDirectory);
        }

        var tool = ToolRegistry.Find(name);
        if (tool is null)
            return Unknown(name, rest, project, global);

        //A misspelled subcommand gets a suggestion too, since fixing only the tool name would be half a fix
        var repaired = RepairSub(tool, rest);
        if (repaired is not null && AskSub(name, rest[0], repaired[0]))
            return tool.Run(repaired);

        return tool.Run(rest);
    }

    //Suggests a close subcommand, returning null when none fits
    //Options and positional arguments are out of scope because the usage text cannot tell them apart from a subcommand
    private static string[]? RepairSub(ToolEntry tool, string[] rest)
    {
        if (rest.Length == 0 || rest[0].StartsWith('-'))
            return null;

        var known = ToolRegistry.SubCommandsOf(tool);

        //a subcommand that already matches is left alone; an exact name is never suggested for itself, so the nearest
        //other one would win and a valid command would be rewritten, sending `template gui` off to `tui`
        if (known.Contains(rest[0], StringComparer.Ordinal))
            return null;

        var suggestion = Similarity.Closest(rest[0], known);
        if (suggestion is null)
            return null;

        var repaired = (string[])rest.Clone();
        repaired[0] = suggestion;
        return repaired;
    }

    //Reports an unknown subcommand and offers to run the suggested one
    //Stays silent when input is redirected so scripts never hit an unanswered prompt
    private static bool AskSub(string tool, string written, string suggestion)
    {
        if (Console.IsInputRedirected)
            return false;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Unknown {tool} subcommand: {written}");
        Console.ResetColor();
        Console.WriteLine($"Did you mean \"{suggestion}\"?");

        return Prompt.Confirm($"Run \"ncm {tool} {suggestion}\" instead?", defaultYes: true, warn: true);
    }

    //Reports an unrecognized name in red and offers a close match when there is one
    //Repairs the subcommand too, so only half the command is not fixed
    //Prints the suggestion when input is redirected instead of deciding for a script
    private static int Unknown(string name, string[] rest, NcProject? project, NcProject? global)
    {
        var suggestion = Similarity.Closest(name, Help.Candidates(project, global));
        var tool = suggestion is null ? null : ToolRegistry.Find(suggestion);
        var repaired = tool is null ? rest : RepairSub(tool, rest) ?? rest;
        var target = suggestion is null ? null : string.Join(' ', new[] { suggestion }.Concat(repaired));

        Help.UnknownTool(Console.Out, name, target);

        if (target is not null && !Console.IsInputRedirected
            && Prompt.Confirm($"Run \"ncm {target}\" instead?", defaultYes: true, warn: true))
            return Dispatch(suggestion!, repaired, project, global);

        Console.WriteLine();
        Help.Print(Console.Out);
        return 1;
    }
}
