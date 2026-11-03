using System.Reflection;
using System.Runtime.Loader;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//Hooks the msbuild assemblies into assembly resolution
//The engine ships beside the tool, so package targets run without a local sdk; an sdk copy is used only when the
//bundled files are absent, such as a build output that was cleaned after the tool was packed
internal static class MsBuildLibrary
{
    //Directory holding the engine, null when neither the tool nor an sdk provides one
    private static string? _directory;
    private static bool _attached;

    public static string? SdkDirectory => _directory ??= Locate();

    //Locates the sdk, hooks the resolve event and reports whether the engine is usable
    //Must run before the first msbuild type is resolved, or loading falls back to default rules and misses that copy
    public static bool Attach(out string error)
    {
        error = string.Empty;
        if (SdkDirectory is null)
        {
            error = "no dotnet sdk found, the msbuild engine is unavailable";
            return false;
        }

        if (_attached)
            return true;

        _attached = true;
        AssemblyLoadContext.Default.Resolving += Resolve;
        Trace.Log($"msbuild engine from {SdkDirectory}");
        return true;
    }

    //Loads a missing assembly from the sdk directory, returning null to fall back to default resolution
    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (SdkDirectory is null || string.IsNullOrEmpty(name.Name))
            return null;

        var path = Path.Combine(SdkDirectory, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    //Picks the bundled engine first, then the highest-version sdk directory
    private static string? Locate()
    {
        //the tool ships the engine so package targets work on a machine that has no sdk at all
        var bundled = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(bundled, "MSBuild.dll")))
            return bundled;

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(root))
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Path.Combine(programFiles, "dotnet");
            if (Directory.Exists(candidate))
                root = candidate;
        }

        var directory = string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, "sdk");
        if (!Directory.Exists(directory))
            return null;

        //Compare by version, not directory name, or 9.x would sort before 10.x
        return Directory.GetDirectories(directory)
            .Where(path => File.Exists(Path.Combine(path, "MSBuild.dll")))
            .OrderByDescending(path => Version.TryParse(Path.GetFileName(path), out var value) ? value : new Version())
            .FirstOrDefault();
    }
}
