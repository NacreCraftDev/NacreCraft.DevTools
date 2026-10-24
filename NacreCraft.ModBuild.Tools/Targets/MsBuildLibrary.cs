using System.Reflection;
using System.Runtime.Loader;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//Hooks the msbuild assemblies bundled with the local sdk into assembly resolution
//ncm does not ship them and the engine must be the sdk's own copy, or the sdk's resolvers and targets break on mismatched interfaces
internal static class MsBuildLibrary
{
    //Local sdk directory, null when this machine has none
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

    //Picks the highest-version sdk directory
    private static string? Locate()
    {
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
