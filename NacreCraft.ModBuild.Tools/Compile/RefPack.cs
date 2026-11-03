using System.Runtime.InteropServices;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Compile;

//Reference assemblies for the current target framework
//Compiling against implementation assemblies instead would bind the output to concrete implementations
//A local SDK already ships the packs directory, and only a miss needs a download
internal static class RefPack
{
    private const string PackName = "Microsoft.NETCore.App.Ref";

    //Located once so repeated compilations in one process do not rescan the directory
    private static IReadOnlyList<string>? _cache;

    //Same for the analyzers beside the pack, which are read by the generator pass
    private static IReadOnlyList<string>? _analyzers;

    //Reference assemblies for the current target framework, or empty when none are found
    public static IReadOnlyList<string> Assemblies()
    {
        if (_cache is not null)
            return _cache;

        _cache = Locate();
        return _cache;
    }

    //Analyzer and generator assemblies of the same pack, which is where the interop generators ship
    public static IReadOnlyList<string> Analyzers()
    {
        if (_analyzers is not null)
            return _analyzers;

        _analyzers = LocateAnalyzers();
        return _analyzers;
    }

    private static IReadOnlyList<string> Locate()
    {
        var root = DotnetRoot();
        if (root is null)
        {
            Trace.Log("cannot locate the dotnet root, reference assemblies are unavailable");
            return [];
        }

        var pack = Path.Combine(root, "packs", PackName);
        if (!Directory.Exists(pack))
        {
            Trace.Log($"no {pack}, the reference pack is not installed");
            return [];
        }

        var framework = TargetFramework();
        //Highest version first, but the framework directory must exist: having only 10.0.0 means 11.0.0 cannot be picked
        var candidates = Directory.GetDirectories(pack)
            .OrderByDescending(path => ParseVersion(Path.GetFileName(path)));

        foreach (var candidate in candidates)
        {
            var reference = Path.Combine(candidate, "ref", framework);
            if (!Directory.Exists(reference))
                continue;

            var files = Directory.EnumerateFiles(reference, "*.dll").ToList();
            if (files.Count == 0)
                continue;

            Trace.Log($"reference pack {reference} ({files.Count} assemblies)");
            return files;
        }

        Trace.Log($"no ref/{framework} under {pack}");
        return [];
    }

    //LocateAnalyzers reads the analyzers of the pack version whose reference assemblies are being used
    //Only the top level of the folder holds assemblies, the subfolders are the localized resource copies
    private static IReadOnlyList<string> LocateAnalyzers()
    {
        var root = DotnetRoot();
        if (root is null)
            return [];

        var pack = Path.Combine(root, "packs", PackName);
        if (!Directory.Exists(pack))
            return [];

        var framework = TargetFramework();
        foreach (var candidate in Directory.GetDirectories(pack)
                     .OrderByDescending(path => ParseVersion(Path.GetFileName(path))))
        {
            if (!Directory.Exists(Path.Combine(candidate, "ref", framework)))
                continue;

            var analyzers = Path.Combine(candidate, "analyzers", "dotnet", "cs");
            if (!Directory.Exists(analyzers))
                continue;

            var files = Directory.EnumerateFiles(analyzers, "*.dll").ToList();
            Trace.Log($"pack analyzers {analyzers} ({files.Count} assemblies)");
            return files;
        }

        return [];
    }

    //dotnet's install root, three levels up from the runtime directory since ncm itself runs on dotnet
    private static string? DotnetRoot()
    {
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        if (string.IsNullOrEmpty(runtime))
            return null;

        //.../dotnet/shared/Microsoft.NETCore.App/<version>/
        var directory = new DirectoryInfo(runtime);
        return directory.Parent?.Parent?.Parent?.FullName;
    }

    //Short framework name such as net10.0
    private static string TargetFramework()
    {
        const string marker = "Version=v";
        var name = AppContext.TargetFrameworkName ?? string.Empty;
        var index = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? "net10.0" : "net" + name[(index + marker.Length)..];
    }

    //Compares directory names as versions, treating unparsable ones as zero
    private static Version ParseVersion(string name)
        => Version.TryParse(name, out var version) ? version : new Version(0, 0);
}
