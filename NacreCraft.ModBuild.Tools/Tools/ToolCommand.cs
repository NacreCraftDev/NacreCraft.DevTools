using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//ToolCommand installs, removes and runs plugins
//add, remove and list are taken, so a plugin cannot be named after one of them
internal static class ToolCommand
{
    private const string AddCommand = "add";
    private const string RemoveCommand = "remove";
    private const string ListCommand = "list";

    //Register this tool with its name, description and parameters
    public static void Register()
        => ToolRegistry.Register("tool", "Install, remove and run ncm plugins", Run,
        [
            new($"{AddCommand} <dll>", "Install a plugin, every dll beside the given one is copied into the tool cache under its own name"),
            new($"{RemoveCommand} <plugin>", "Remove an installed plugin"),
            new(ListCommand, "List the installed plugins"),
            new("<plugin> <method> [args]", "Run an installed plugin, everything after the method is passed to it unchanged"),
        ]);

    //Run dispatches the subcommand, anything that is not one of them names a plugin
    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        return args[0] switch
        {
            AddCommand => Add(args),
            RemoveCommand => Remove(args),
            ListCommand => List(),
            _ => Call(args),
        };
    }

    //Add installs a plugin from a dll
    private static int Add(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("error: a dll path is required");
            Usage();
            return 1;
        }

        if (PluginHost.Install(args[1], out var error))
            return 0;

        Console.WriteLine($"error: {error}");
        return 1;
    }

    //Remove deletes an installed plugin
    private static int Remove(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("error: a plugin name is required");
            Usage();
            return 1;
        }

        if (PluginHost.Remove(args[1], out var error))
            return 0;

        Console.WriteLine($"error: {error}");
        return 1;
    }

    //List prints what is installed, one per line
    private static int List()
    {
        var installed = PluginHost.Names();
        if (installed.Count == 0)
        {
            Console.WriteLine($"No plugin installed, add one with 'ncm tool {AddCommand} <dll path>'");
            return 0;
        }

        foreach (var name in installed)
            Console.WriteLine(name);
        return 0;
    }

    //Call runs an installed plugin
    private static int Call(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("error: a method is required");
            Usage();
            return 1;
        }

        var code = PluginHost.Run(args[0], args[1], args[2..], out var error);
        if (!string.IsNullOrEmpty(error))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: {error}");
            Console.ResetColor();
        }

        return code;
    }

    //Usage prints the accepted forms
    private static void Usage()
    {
        Console.WriteLine($"Usage: ncm tool {AddCommand} <dll>");
        Console.WriteLine($"       ncm tool {RemoveCommand} <plugin>");
        Console.WriteLine($"       ncm tool {ListCommand}");
        Console.WriteLine($"       ncm tool <plugin> <method> [args]");
    }
}
