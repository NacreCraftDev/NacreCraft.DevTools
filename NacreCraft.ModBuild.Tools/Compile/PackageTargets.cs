using System.Text.RegularExpressions;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Compile;

//Collects the props and targets a package ships into the project
//Doing so makes the project self contained, so clearing the package cache or moving machines still builds
//Files reached through MSBuildThisFileDirectory come along too, otherwise task assemblies go missing
internal static class PackageTargets
{
    //Target directory for collected files, the targets folder under the project's Build
    private static string DirectoryOf(string root) => Path.Combine(root, ProjectLayout.Targets);

    //By nuget convention direct references read build and transitive ones read buildTransitive, and taking both would import the same targets twice
    private static readonly string[] DirectFolders = ["build", "buildMultiTargeting"];
    private static readonly string[] TransitiveFolders = ["buildTransitive"];

    //Collects one package and returns how many files came in
    public static int Collect(string root, string source, string id, bool direct)
    {
        var destination = Path.Combine(DirectoryOf(root), id);
        var copied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in direct ? DirectFolders : TransitiveFolders)
        {
            var directory = Path.Combine(source, folder);
            if (!Directory.Exists(directory))
                continue;

            var files = Directory.EnumerateFiles(directory, "*.props", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(directory, "*.targets", SearchOption.AllDirectories))
                .ToList();

            foreach (var file in files)
            {
                Copy(file, source, destination, copied);
                foreach (var reference in References(file))
                    Copy(reference, source, destination, copied);
            }
        }

        return copied.Count;
    }

    //Imports collected into the project with props always ahead of targets, ordered by package name and then by path within a package
    public static IEnumerable<string> Imports(string root)
    {
        var directory = DirectoryOf(root);
        if (!Directory.Exists(directory))
            yield break;

        foreach (var pattern in new[] { "*.props", "*.targets" })
        {
            var files = Directory.EnumerateDirectories(directory)
                .Where(path => !LooksLikeIntermediate(path))
                .SelectMany(path => Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
                yield return file;
        }
    }

    //Intermediate folders are not packages, so their contents must not be taken as imports
    private static bool LooksLikeIntermediate(string path)
        => Path.GetFileName(path).Equals("obj", StringComparison.OrdinalIgnoreCase);

    //Copies preserving the in package layout because targets locate each other through relative paths
    private static void Copy(string path, string source, string destination, HashSet<string> copied)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(source, StringComparison.OrdinalIgnoreCase))
            return;

        var target = Path.Combine(destination, Path.GetRelativePath(source, full));
        if (!copied.Add(target))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(full, target, overwrite: true);
        }
        catch (IOException e)
        {
            Trace.Log($"cannot collect {full}: {e.Message}");
        }
    }

    //Pulls the paths referenced through MSBuildThisFileDirectory out of the text, skipping ones containing properties since the package itself will complain
    private static IEnumerable<string> References(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            yield break;
        }

        var directory = Path.GetDirectoryName(path)!;
        foreach (Match match in Regex.Matches(text, @"\$\(MSBuildThisFileDirectory\)([^""'<>;\s]*)", RegexOptions.IgnoreCase))
        {
            //The tail often starts with a separator, which would drop the leading part if combined directly
            var tail = match.Groups[1].Value.TrimStart('\\', '/');
            if (tail.Length == 0 || tail.Contains("$(") || tail.IndexOfAny(['*', '?']) >= 0)
                continue;

            var candidate = Path.GetFullPath(Path.Combine(directory, tail));
            if (File.Exists(candidate))
            {
                yield return candidate;
                continue;
            }

            if (!Directory.Exists(candidate))
                continue;

            foreach (var file in Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories))
                yield return file;
        }
    }
}
