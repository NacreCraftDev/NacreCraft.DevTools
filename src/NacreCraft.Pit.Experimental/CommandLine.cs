using NacreCraft.Pit.Core;
using NacreCraft.Pit.Tools;

namespace NacreCraft.Pit.Experimental;

//Dispatch for the experimental command line
//Project tasks resolve first, then the machine wide tasks, then whatever sits in the shared tool registry,
//which is the same order the shipped command line uses
internal static class CommandLine
{
    public static int Run(string name, string[] rest, NcProject? project, NcProject? global)
    {
        //a bootstrap exposes the tasks of its projects as <bootstrap>/<task>
        if (project is { Bootstrap: true } && project.FindScopedTask(name) is { } scoped)
            return TaskRunner.Run(scoped.Project, scoped.Task);

        if (project?.FindTask(name) is { } task)
            return TaskRunner.Run(project!, task);

        if (global?.FindTask(name) is { } shared)
            return TaskRunner.Run(global!, shared, Environment.CurrentDirectory);

        if (ToolRegistry.Find(name) is { } tool)
            return tool.Run(rest);

        //an unknown name only gets the full list here; the shipped command line also offers repairs,
        //which an experimental build does not need to carry yet
        Help.UnknownTool(Console.Out, name, null);
        Console.WriteLine();
        Help.Print(Console.Out, Program.Name);
        return 1;
    }
}
