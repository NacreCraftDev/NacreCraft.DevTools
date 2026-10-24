using NacreCraft.ModBuild.Compile;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//RestoreTool restores the packages declared in the project config
//The result is the compile-time references that later get embedded into the mod assembly
internal static class RestoreTool
{
    //DefaultDirectory is the restore folder used when no directory is given
    private static readonly string DefaultDirectory = ProjectLayout.Packages;

    public static void Register()
        => ToolRegistry.Register("restore", "Resolve the declared packages and copy their assemblies into the project", Run,
        [
            new("[directory]", $"Where to restore, relative to the project root, defaults to {DefaultDirectory}"),
        ]);

    //Run finds the project, resolves the target directory and hands off to the resolver
    private static int Run(string[] args)
    {
        if (args.Length > 1)
        {
            Console.WriteLine("Usage: ncm restore [directory]");
            return 1;
        }

        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        if (project is null)
        {
            Console.WriteLine($"error: no {NcProject.Extension} in this directory or any parent");
            return 1;
        }

        var directory = DirectoryOf(project, args.Length == 1 ? args[0] : null);
        Console.WriteLine($"Restoring packages of {new DirectoryInfo(project.Directory).Name} into {directory}");

        if (!Ensure(project, directory, out var error))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: {error}");
            Console.ResetColor();
            return 1;
        }

        return 0;
    }

    //Ensure restores the project dependencies and short-circuits when they are already in place
    //The build path relies on this too, so there is a single restore implementation
    internal static bool Ensure(NcProject project, string directory, out string error)
    {
        error = string.Empty;
        if (Ready(project, directory))
        {
            Trace.Log($"packages of {project.Directory} are ready in {directory}");
            return true;
        }

        return PackageResolver.Restore(project, directory, out error);
    }

    //DirectoryOf is the restore destination, the given directory or the default
    internal static string DirectoryOf(NcProject project, string? directory = null)
        => Path.GetFullPath(Path.Combine(project.Directory,
            string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory));

    //Ready checks that every declared package already sits in the directory
    //Transitive dependencies follow the declared ones, so only declared packages are checked here
    private static bool Ready(NcProject project, string directory)
    {
        if (project.Packages.Count == 0 || !Directory.Exists(directory))
            return false;

        var folders = Directory.EnumerateDirectories(directory)
            .Select(Path.GetFileName)
            .ToList();

        foreach (var package in project.Packages)
        {
            if (!folders.Any(name => string.Equals(name, package.Id, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }
}
