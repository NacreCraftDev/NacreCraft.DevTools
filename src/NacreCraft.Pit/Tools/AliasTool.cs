using NacreCraft.Pit.Core;

namespace NacreCraft.Pit.Tools;

//AliasTool writes the mpit shortcut beside the installed pit
//A script instead of a second tool package, so dotnet tool update replaces the target and leaves the shortcut working
internal static class AliasTool
{
    private const string RemoveOption = "--remove";

    private const string AliasName = "mpit";

    //Register this tool with its name, description and parameters
    public static void Register()
        => ToolRegistry.Register("alias", "Create or remove the mpit shortcut in the directory pit is installed in", Run,
        [
            new(RemoveOption, "Delete the shortcut instead of writing it"),
        ]);

    //Run writes the shortcut, or removes it when asked
    private static int Run(string[] args)
    {
        if (args.Any(argument => !string.Equals(argument, RemoveOption, StringComparison.Ordinal)))
        {
            Console.WriteLine($"Usage: pit alias [{RemoveOption}]");
            return 1;
        }

        var remove = args.Contains(RemoveOption, StringComparer.Ordinal);
        var directory = ToolDirectory();
        if (directory is null)
        {
            Console.WriteLine("error: pit is not running from an installed shim, install it with 'dotnet tool install -g NacreCraft.Pit' first");
            return 1;
        }

        var path = Path.Combine(directory, OperatingSystem.IsWindows() ? AliasName + ".cmd" : AliasName);
        try
        {
            if (remove)
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"{path} does not exist");
                    return 0;
                }

                File.Delete(path);
                Console.WriteLine($"Removed {path}");
                return 0;
            }

            var content = Shortcut();
            if (File.Exists(path) && File.ReadAllText(path) == content)
            {
                //the executable bit can be lost by a copy, so it is restored even when the text already matches
                SetExecutable(path);
                Console.WriteLine($"{path} is already up to date");
                return 0;
            }

            File.WriteAllText(path, content);
            SetExecutable(path);
            Console.WriteLine($"Wrote {path}");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    //The shim folder, null when the process was started some other way
    //A shortcut written beside a build output would never be on PATH, so only the installed name is accepted
    private static string? ToolDirectory()
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
            return null;

        return string.Equals(Path.GetFileNameWithoutExtension(executable), "pit", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(executable)
            : null;
    }

    //Shortcut calls the shim beside itself, so a dotnet tool update keeps working without rewriting the shortcut
    private static string Shortcut()
        => OperatingSystem.IsWindows()
            ? "@echo off\r\n\"%~dp0pit.exe\" %*\r\n"
            : "#!/bin/sh\nexec \"$(dirname \"$0\")/pit\" \"$@\"\n";

    //Only unix carries an executable bit, which the shim needs to be started by name
    private static void SetExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
