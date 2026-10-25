using NacreCraft.ModBuild.Core;
using System.Text;

namespace NacreCraft.ModBuild.Tools;

//BatchTool runs several tasks in one process, so the engine is set up once instead of once per task
//Each task starts from the same project and global context, which is what makes repeated checks cheap
internal static class BatchTool
{
    public static void Register()
        => ToolRegistry.Register("batch", "Run several tasks in one process, separated by &", Run,
        [
            new("--continue-on-error", "Keep going after a task fails instead of stopping"),
        ]);

    private static int Run(string[] args)
    {
        var continueOnError = false;
        var text = new StringBuilder();
        foreach (var arg in args)
        {
            if (arg == "--continue-on-error")
            {
                continueOnError = true;
                continue;
            }

            if (text.Length > 0)
                text.Append(' ');
            text.Append(arg);
        }

        var tasks = Split(text.ToString());
        if (tasks.Count == 0)
        {
            Console.WriteLine("error: batch needs at least one task");
            Usage();
            return 1;
        }

        var project = NcProject.TryFind(Environment.CurrentDirectory, out var error);
        if (!string.IsNullOrEmpty(error))
        {
            Console.WriteLine($"error: {error}");
            return 1;
        }

        var global = GlobalConfig.Load();
        var failure = 0;
        var index = 0;
        foreach (var task in tasks)
        {
            index++;
            var parts = ArgumentLine.Split(task);
            if (parts.Length == 0)
                continue;

            var code = Program.Dispatch(parts[0], parts[1..], project, global);
            if (code == 0)
                continue;

            if (failure == 0)
                failure = code;
            if (continueOnError)
                continue;

            Console.WriteLine($"error: task {index} exited with code {code}");
            return code;
        }

        return failure;
    }

    //Split cuts at every & outside a quote, so a task keeps its own quoted arguments intact
    private static List<string> Split(string text)
    {
        var tasks = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\\' && index + 1 < text.Length && text[index + 1] is '&' or '"' or '\'')
            {
                current.Append(text[++index]);
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                current.Append(character);
                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                current.Append(character);
                continue;
            }

            if (character == '&')
            {
                Add(tasks, current);
                continue;
            }

            current.Append(character);
        }

        Add(tasks, current);
        return tasks;
    }

    private static void Add(List<string> tasks, StringBuilder current)
    {
        var task = current.ToString().Trim();
        current.Clear();
        if (task.Length > 0)
            tasks.Add(task);
    }

    private static void Usage()
        => Console.WriteLine("Usage: ncm batch [--continue-on-error] <task>[&<task>...]");
}
