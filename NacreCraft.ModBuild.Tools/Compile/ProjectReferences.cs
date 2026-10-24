using System.Diagnostics;
using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Tools;

namespace NacreCraft.ModBuild.Compile;

//Handles the <Project> entries under <References>
//A referenced project is built first and only its output is used as a compile reference, never embedded or deployed
//References nest recursively and cycles are caught through Building
internal static class ProjectReferences
{
    //Build configuration for a referenced project that is still a plain csproj, which has no ncproj config to read from
    private const string CsprojConfiguration = "Release";

    //Project directories currently being built, used to catch cycles
    private static readonly HashSet<string> Building = new(StringComparer.OrdinalIgnoreCase);

    //Project directory to output path, resolving each reference once per run and caching failures too so the same error is not reported repeatedly
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    //Reset before each build so results do not leak between runs
    public static void Clear()
    {
        Building.Clear();
        Cache.Clear();
    }

    //Prepares every project reference declared in the config and reports success
    public static bool Build(string root, NcProject project, out string error)
    {
        error = string.Empty;
        foreach (var include in project.ProjectReferences)
        {
            if (Resolve(root, include, out error) is null)
                return false;
        }

        return true;
    }

    //Output assemblies of these references, ignoring anything that failed to resolve
    public static IEnumerable<string> Products(string root, NcProject project)
    {
        foreach (var include in project.ProjectReferences)
        {
            var directory = DirectoryOf(root, include);
            if (directory is not null
                && Cache.TryGetValue(directory, out var product)
                && product is not null)
                yield return product;
        }
    }

    //Resolves one project reference, building it when needed
    private static string? Resolve(string root, string include, out string error)
    {
        error = string.Empty;
        var directory = DirectoryOf(root, include);
        if (directory is null)
        {
            error = $"the project referenced as \"{include}\" does not exist";
            return null;
        }

        if (Cache.TryGetValue(directory, out var cached))
            return cached;

        if (!Building.Add(directory))
        {
            error = $"circular project reference through \"{include}\"";
            return null;
        }

        try
        {
            var product = Compile(directory, out error);
            Cache[directory] = product;
            return product;
        }
        finally
        {
            Building.Remove(directory);
        }
    }

    //Directory a reference points at, accepting either a project directory or a project file
    private static string? DirectoryOf(string root, string include)
    {
        var path = Path.GetFullPath(Path.Combine(root, include));
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        return directory is not null && Directory.Exists(directory) ? directory : null;
    }

    //Builds a referenced project and returns its output assembly; ncproj goes through the direct compiler and a plain csproj through dotnet
    private static string? Compile(string directory, out string error)
    {
        error = string.Empty;
        //TryFind walks upward, so a config found in a parent project means this directory has none
        var config = NcProject.TryFind(directory, out var configError);
        if (config is not null && SamePath(config.Directory, directory))
            return CompileProject(directory, config, out error);

        if (!string.IsNullOrEmpty(configError))
        {
            error = configError;
            return null;
        }

        return CompileCsproj(directory, out error);
    }

    //Direct compile path, provisioning the kernel, packages and its own references in the same order as the main project
    private static string? CompileProject(string directory, NcProject config, out string error)
    {
        if (!KernelStore.Sync(directory, config))
        {
            error = $"cannot prepare the kernel of {directory}";
            return null;
        }

        if (!RestoreTool.Ensure(config, RestoreTool.DirectoryOf(config), out var restoreError))
        {
            error = restoreError;
            return null;
        }

        if (!Build(directory, config, out error))
            return null;

        var name = string.IsNullOrWhiteSpace(config.Build.AssemblyName)
            ? new DirectoryInfo(directory).Name
            : config.Build.AssemblyName;
        var target = Path.Combine(directory, config.Build.Output, name + ".dll");

        Console.WriteLine($"Building referenced project {new DirectoryInfo(directory).Name}");
        return Compiler.Compile(directory, config, name, target, null, out error) ? target : null;
    }

    //Projects not yet migrated go through dotnet, with the output found under bin/<configuration> by project name since the framework folder name is unknown
    private static string? CompileCsproj(string directory, out string error)
    {
        error = string.Empty;
        var project = Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault();
        if (project is null)
        {
            error = $"no {NcProject.Extension} or .csproj found in {directory}";
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(project);
        Console.WriteLine($"Building referenced project {name} with dotnet");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
        };
        //Keep child process output in English regardless of the system language
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(CsprojConfiguration);
        startInfo.ArgumentList.Add("--nologo");

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            error = "could not start dotnet";
            return null;
        }

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            error = $"dotnet build of {name} exited with code {process.ExitCode}";
            return null;
        }

        var bin = Path.Combine(directory, "bin", CsprojConfiguration);
        var product = Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, name + ".dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (product is null)
        {
            error = $"no {name}.dll under bin/{CsprojConfiguration} of {directory}";
            return null;
        }

        return product;
    }

    //Whether two directories are the same, since a reference must not resolve to a parent project
    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
