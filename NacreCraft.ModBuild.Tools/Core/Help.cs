using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Core;

//Help text printed when no argument is given or the argument is unknown
public static class Help
{
    //Command line shape; each tool parses the arguments that follow its name
    public const string Usage = "ncm <tool> [options]";

    //Print every tool with its one-line description, plus the current project's own tasks when inside one
    public static void Print(TextWriter writer)
    {
        writer.WriteLine("NacreCraft mod development tools");
        writer.WriteLine();
        writer.WriteLine($"Usage: {Usage}");
        writer.WriteLine();
        writer.WriteLine("Tools:");
        foreach (var tool in ToolRegistry.All)
            writer.WriteLine($"  {tool.Name,-12} {tool.Description}");

        WriteProjectTasks(writer);
        WriteGlobalTasks(writer);

        writer.WriteLine();
        writer.WriteLine("Run 'ncm help <tool>' to see the parameters of one tool, or 'ncm help all' for every tool.");
    }

    //PrintAll every registered tool's full help, the same text as running ncm help <tool> one by one
    public static void PrintAll(TextWriter writer)
    {
        var first = true;
        foreach (var tool in ToolRegistry.All)
        {
            if (!first)
                writer.WriteLine();

            first = false;
            PrintTool(writer, tool.Name);
        }
    }

    //Report an unrecognized name in red, adding a suggestion when a close match exists
    public static void UnknownTool(TextWriter writer, string name, string? suggestion)
    {
        Red(writer, $"Unknown tool: {name}");
        if (!string.IsNullOrEmpty(suggestion))
            writer.WriteLine($"Did you mean \"{suggestion}\"?");
    }

    //Names eligible as suggestions, built-in tools plus the runnable tasks of the project and of the machine
    public static List<string> Candidates(NcProject? project, NcProject? global)
    {
        var names = ToolRegistry.All.Select(entry => entry.Name).ToList();
        if (project is not null)
        {
            names.AddRange(project.Runnable().Select(task => task.Name));
            names.AddRange(project.ScopedTasks().Select(task => task.Name));
        }
        if (global is not null)
            names.AddRange(global.Runnable().Select(task => task.Name));
        return names;
    }

    //Print one tool's parameter help, falling back to a task when no built-in matches, and return the process exit code
    public static int PrintTool(TextWriter writer, string name)
    {
        //Same precedence as execution, an exact project task wins over the global one and over a loose built-in match
        var project = NcProject.TryFind(Environment.CurrentDirectory, out _);
        var global = GlobalConfig.Load();
        var task = project?.FindTask(name) ?? global?.FindTask(name);
        if (task is not null)
        {
            WriteTask(writer, task);
            return 0;
        }

        //a bootstrap task such as xxx/build is printed like the child task it names
        if (project?.FindScopedTask(name) is { } scoped)
        {
            WriteTask(writer, scoped.Task);
            return 0;
        }

        var tool = ToolRegistry.Find(name);
        if (tool is null)
        {
            UnknownTool(writer, name, Similarity.Closest(name, Candidates(project, global)));
            return 1;
        }

        writer.WriteLine($"{tool.Name} - {tool.Description}");
        writer.WriteLine();
        if (tool.Parameters.Count == 0)
        {
            writer.WriteLine("This tool takes no parameters.");
            return 0;
        }

        writer.WriteLine("Parameters:");
        //Align the two columns on the longest syntax by padding shorter names
        var width = tool.Parameters.Max(item => item.Syntax.Length);
        foreach (var parameter in tool.Parameters)
            writer.WriteLine($"  {parameter.Syntax.PadRight(width)}  {parameter.Description}");
        return 0;
    }

    //Append the project's tasks, warning when a name shadows a built-in
    private static void WriteProjectTasks(TextWriter writer)
    {
        var project = NcProject.TryFind(Environment.CurrentDirectory, out var error);
        if (!string.IsNullOrEmpty(error))
        {
            writer.WriteLine();
            Warning(writer, error);
            return;
        }

        if (project is null)
            return;

        var tasks = project.Runnable();
        var scoped = project.ScopedTasks();
        if (tasks.Count > 0 || scoped.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Project tasks:");
            var width = Math.Max(
                tasks.Count > 0 ? tasks.Max(task => task.Name.Length) : 0,
                scoped.Count > 0 ? scoped.Max(task => task.Name.Length) : 0);

            foreach (var task in tasks)
                writer.WriteLine($"  {task.Name.PadRight(width)}  {task.Title}");

            //a bootstrap names the tasks of its projects with its own prefix
            foreach (var task in scoped)
                writer.WriteLine($"  {task.Name.PadRight(width)}  {task.Task.Title}");
        }

        foreach (var task in project.Shadowed())
            Warning(writer, $"task {task.Name} conflicts with a built-in tool name, consider renaming it");
    }

    //Append the tasks of the machine wide config, which are reachable from any directory and shown with their source
    private static void WriteGlobalTasks(TextWriter writer)
    {
        var global = GlobalConfig.Load();
        if (global is null)
            return;

        var tasks = global.Runnable();
        if (tasks.Count == 0)
            return;

        writer.WriteLine();
        writer.WriteLine($"Global tasks from {GlobalConfig.Location}:");
        var width = tasks.Max(task => task.Name.Length);
        foreach (var task in tasks)
            writer.WriteLine($"  {task.Name.PadRight(width)}  {task.Title}");
    }

    //Print a project task's title, dependencies and steps
    private static void WriteTask(TextWriter writer, NcTask task)
    {
        writer.WriteLine($"{task.Name} - {task.Title}");
        if (task.Depends.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"Depends on: {string.Join(", ", task.Depends)}");
        }

        writer.WriteLine();
        if (task.Steps.Count == 0)
        {
            writer.WriteLine("This task has no step.");
            return;
        }

        writer.WriteLine("Steps:");
        foreach (var step in task.Steps)
            writer.WriteLine($"  {step.Text}");
    }

    private static void Warning(TextWriter writer, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        writer.WriteLine($"warning: {message}");
        Console.ForegroundColor = previous;
    }

    private static void Red(TextWriter writer, string message)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        writer.WriteLine(message);
        Console.ForegroundColor = previous;
    }
}
