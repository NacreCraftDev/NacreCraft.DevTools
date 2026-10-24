using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Diagnostics;

//SourceFiles enumerates the project's source files
//any path segment named bin or obj is skipped as build output, including those under nested projects
//the project config can exclude more through <Exclude>, which keeps a directory out of the compile and the checks alike
internal static class SourceFiles
{
    //SkippedDirectories holds build output directory names compared as whole segments so names like binary are not caught
    private static readonly string[] SkippedDirectories = { "bin", "obj" };

    //Excluded holds the compiled patterns from <Exclude>, empty when the config declares none
    private static readonly List<Func<string, bool>> Excluded = new();

    //Configure takes the exclusion patterns from the project config and from the caller
    //Called once per run before anything enumerates, the same way the other stores take their config
    //ignoreConfig drops the config patterns, which lets a tool read every source file on purpose
    public static void Configure(NcProject? project, IEnumerable<string>? extra = null, bool ignoreConfig = false)
    {
        Excluded.Clear();

        if (project is not null && !ignoreConfig)
        {
            foreach (var pattern in project.Exclude)
                Excluded.Add(PathPattern.Compile(pattern));
        }

        if (extra is not null)
        {
            foreach (var pattern in extra)
                Excluded.Add(PathPattern.Compile(pattern));
        }
    }

    //Enumerate lists all .cs files in the project
    public static IEnumerable<string> Enumerate(string root) => Enumerate(root, "*.cs");

    //Enumerate overload taking a pattern, used for files like the axaml the generator reads
    public static IEnumerable<string> Enumerate(string root, string pattern)
    {
        foreach (var path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
        {
            if (IsGenerated(root, path) || IsExcluded(root, path))
                continue;

            yield return path;
        }
    }

    //IsGenerated checks whether the path relative to root runs through a build output directory
    internal static bool IsGenerated(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => SkippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    //IsExcluded checks the path against the configured patterns
    //Only what ncm scans on its own is filtered here, patterns declared in the config are still honored as written
    internal static bool IsExcluded(string root, string path)
    {
        if (Excluded.Count == 0)
            return false;

        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return Excluded.Any(match => match(relative));
    }
}
