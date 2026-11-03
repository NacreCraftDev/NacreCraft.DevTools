using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Tools;

namespace NacreCraft.ModBuild.Tools.Experimental;

//Entry point of the experimental command line
//Stable tools are registered from the shipped project and experimental ones are added on top, so every
//command a user already knows keeps working from here while the experiments stay unreleased
public static class Program
{
    //the name the help text addresses, because the assembly name is too long to type in a usage line
    internal const string Name = "ncm-exp";

    //The windowing code needs this, the same way the shipped entry point does
    [STAThread]
    public static int Main(string[] args)
    {
        //the progress line draws braille, which the default console code page turns into question marks
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            //no console to set it on, such as a detached pipe
        }

        //the engine comes from the local sdk, and the resolve event must be hooked before msbuild loads
        MsBuildLibrary.Attach(out _);

        ToolRegistration.RegisterAll();
        ExperimentalRegistration.RegisterAll();
        ToolFilter.Apply();

        var global = GlobalConfig.Load();

        if (args.Length == 0)
        {
            Banner();
            Help.Print(Console.Out, Name);
            return 0;
        }

        if (args[0] is "help" or "--help" or "-h")
        {
            if (args.Length > 1 && string.Equals(args[1], "all", StringComparison.OrdinalIgnoreCase))
            {
                Help.PrintAll(Console.Out);
                return 0;
            }

            if (args.Length > 1)
                return Help.PrintTool(Console.Out, args[1]);

            Banner();
            Help.Print(Console.Out, Name);
            return 0;
        }

        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        return CommandLine.Run(args[0], args[1..], project, global);
    }

    //Banner marks the build as experimental, so a copied output is never mistaken for a release
    private static void Banner()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("experimental build, commands here are not part of the released ncm");
        Console.ResetColor();
        Console.WriteLine();
    }
}
