using NacreCraft.ModBuild.Core;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NacreCraft.ModBuild.Compile;

//Resolves the packages declared in the config and restores them into a directory
//Reuses the global package cache and only goes online on a miss, with versions decided by transitive dependencies and ranges
//The target is fixed so compile and pack scan it directly instead of tracking a manifest
public static class PackageResolver
{
    //ncm's own package cache where global cache misses land, so they are not downloaded twice
    private static string Root => CacheLayout.Packages;

    public static string CacheRoot => Root;

    private const string Source = "https://api.nuget.org/v3/index.json";

    //Iteration cap for the dependency graph, which needs a stopping point when versions pull against each other
    private const int Rounds = 16;

    private static readonly ILogger Logger = NullLogger.Instance;

    //Resolves and restores; target is the restore directory where each package takes a subdirectory named after its id
    public static bool Restore(NcProject project, string target, out string error)
    {
        error = string.Empty;
        if (project.Packages.Count == 0)
        {
            Console.WriteLine("No <Package> declared in the project config, nothing to restore");
            return true;
        }

        try
        {
            var (ok, failure) = RestoreAsync(project, target).GetAwaiter().GetResult();
            error = failure;
            return ok;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            Trace.Log($"restore failed: {error}");
            return false;
        }
    }

    private static async Task<(bool Ok, string Error)> RestoreAsync(NcProject project, string target)
    {
        var repository = Repository.Factory.GetCoreV3(Source);
        var cache = new SourceCacheContext();
        var finder = await repository.GetResourceAsync<FindPackageByIdResource>().ConfigureAwait(false);
        if (finder is null)
            return (false, "the package source has no finder resource");

        var packages = await ResolveAsync(project, finder, cache).ConfigureAwait(false);
        if (packages is null)
            return (false, "cannot resolve the package graph");

        var folder = GlobalPackagesFolder();

        foreach (var (id, version) in packages.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var directory = await EnsureAsync(finder, id, version, folder, cache).ConfigureAwait(false);
            if (directory is null)
                return (false, $"cannot get {id} {version.ToNormalizedString()}");

            var files = Copy(directory, id, target);
            var analyzers = CopyAnalyzers(directory, id, target);

            //Collect the package's props and targets into the project so building no longer depends on the package cache
            //Packages named in the config are direct and read build, the rest are transitive and read buildTransitive
            var direct = project.Packages.Any(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            var collected = PackageTargets.Collect(project.Directory, directory, id, direct);

            Console.WriteLine($"Restored {id} {version.ToNormalizedString()} {Summarize(files.Count, analyzers, collected)}");
        }

        return (true, string.Empty);
    }

    private static string Summarize(int assemblies, int analyzers, int targets)
    {
        var parts = new List<string> { $"{assemblies} assembly(ies)" };
        if (analyzers > 0)
            parts.Add($"{analyzers} analyzer(s)");
        if (targets > 0)
            parts.Add($"{targets} build file(s)");

        return "(" + string.Join(", ", parts) + ")";
    }

    //Picks a version for the declared packages and their transitive dependencies, taking the highest version that satisfies every requested range and iterating until stable
    private static async Task<Dictionary<string, NuGetVersion>?> ResolveAsync(NcProject project,
        FindPackageByIdResource finder, SourceCacheContext cache)
    {
        var constraints = new Dictionary<string, List<VersionRange>>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in project.Packages)
        {
            if (string.IsNullOrWhiteSpace(package.Version))
            {
                Constrain(constraints, package.Id, VersionRange.All);
                continue;
            }

            if (!VersionRange.TryParse(package.Version, out var range))
            {
                Console.WriteLine($"error: {package.Id} has an invalid version range \"{package.Version}\"");
                return null;
            }

            Constrain(constraints, package.Id, range);
        }

        var chosen = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var expanded = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);

        for (var round = 0; round < Rounds; round++)
        {
            var changed = false;
            foreach (var (id, ranges) in constraints.ToList())
            {
                var versions = await finder.GetAllVersionsAsync(id, cache, Logger, default).ConfigureAwait(false) ?? [];
                var candidates = versions.Where(candidate => ranges.All(range => range.Satisfies(candidate))).ToList();

                //A missing version takes the latest while an explicit one takes the lowest satisfying version, matching NuGet where 13.0.3 means [13.0.3,)
                var version = ranges.All(range => range.Equals(VersionRange.All))
                    ? candidates.Max()
                    : candidates.Min();

                if (version is null)
                {
                    Console.WriteLine($"error: no version of {id} satisfies the requested range");
                    return null;
                }

                chosen[id] = version;

                //Each dependency version expands once, otherwise the graph would keep looping
                if (expanded.TryGetValue(id, out var done) && done == version)
                    continue;

                expanded[id] = version;
                changed = true;

                var info = await finder.GetDependencyInfoAsync(id, version, cache, Logger, default).ConfigureAwait(false);
                foreach (var dependency in Dependencies(info, TargetFramework.NuGet))
                    Constrain(constraints, dependency.Id, dependency.VersionRange);
            }

            if (!changed)
                break;
        }

        return chosen;
    }

    //Picks the dependency group closest to the target framework
    private static IEnumerable<PackageDependency> Dependencies(FindPackageByIdDependencyInfo info,
        NuGetFramework framework)
    {
        var groups = info.DependencyGroups.ToList();
        if (groups.Count == 0)
            yield break;

        var reducer = new FrameworkReducer();
        var nearest = reducer.GetNearest(framework, groups.Select(group => group.TargetFramework));
        if (nearest is null)
            yield break;

        var group = groups.FirstOrDefault(item => item.TargetFramework.Equals(nearest));
        if (group is null)
            yield break;

        foreach (var dependency in group.Packages)
            yield return dependency;
    }

    private static void Constrain(Dictionary<string, List<VersionRange>> constraints, string id, VersionRange range)
    {
        if (!constraints.TryGetValue(id, out var ranges))
        {
            ranges = new List<VersionRange>();
            constraints[id] = ranges;
        }

        if (!ranges.Any(existing => existing.Equals(range)))
            ranges.Add(range);
    }

    //Produces a package directory with readable assemblies, preferring the global cache that dotnet build usually fills and downloading into ncm's own cache on a miss
    private static async Task<string?> EnsureAsync(FindPackageByIdResource finder, string id, NuGetVersion version,
        string globalFolder, SourceCacheContext cache)
    {
        var cached = Path.Combine(globalFolder, id.ToLowerInvariant(), version.ToNormalizedString().ToLowerInvariant());
        if (HasAssemblies(cached))
        {
            Trace.Log($"package cache hit {cached}");
            return cached;
        }

        var directory = Path.Combine(Root, id, version.ToNormalizedString());
        if (HasAssemblies(directory))
            return directory;

        if (await DownloadAsync(finder, id, version, directory, cache).ConfigureAwait(false))
            return directory;

        Console.WriteLine($"error: cannot fetch {id} {version.ToNormalizedString()}");
        return null;
    }

    private static bool HasAssemblies(string directory)
        => Directory.Exists(Path.Combine(directory, "lib")) || Directory.Exists(Path.Combine(directory, "ref"));

    private static async Task<bool> DownloadAsync(FindPackageByIdResource finder, string id, NuGetVersion version,
        string directory, SourceCacheContext cache)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"{id}.{version.ToNormalizedString()}.{Guid.NewGuid():N}.nupkg");
        try
        {
            using (var stream = File.Create(temporary))
            {
                if (!await finder.CopyNupkgToStreamAsync(id, version, stream, cache, Logger, default).ConfigureAwait(false))
                    return false;
            }

            Extract(temporary, directory);
            return true;
        }
        catch (Exception e)
        {
            Trace.Log($"cannot download {id} {version}: {e.GetType().Name}");
            return false;
        }
        finally
        {
            Discard(temporary);
        }
    }

    //Extracts the lib and ref assemblies keeping their layout, the only parts a mod needs
    private static void Extract(string archive, string directory)
    {
        using var reader = new PackageArchiveReader(archive);
        foreach (var entry in reader.GetFiles())
        {
            if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!entry.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                && !entry.StartsWith("ref/", StringComparison.OrdinalIgnoreCase))
                continue;

            var target = Path.Combine(directory, entry.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var source = reader.GetStream(entry);
            using var destination = File.Create(target);
            source.CopyTo(destination);
        }
    }

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    //Copies the package assemblies under a directory named after the package and returns their full paths
    private static List<string> Copy(string packageDirectory, string id, string target)
    {
        var source = Choose(packageDirectory);
        if (source.Count == 0)
        {
            Trace.Log($"no assembly for {TargetFramework.Name} in {packageDirectory}");
            return [];
        }

        var destination = Path.Combine(target, id);
        Directory.CreateDirectory(destination);

        var files = new List<string>();
        foreach (var path in source)
        {
            var copy = Path.Combine(destination, Path.GetFileName(path));
            try
            {
                File.Copy(path, copy, overwrite: true);
                files.Add(copy);
            }
            catch (IOException e)
            {
                Trace.Log($"cannot copy {path}: {e.Message}");
            }
        }

        return files;
    }

    //Picks the lib framework closest to the target and falls back to ref when a package ships only reference assemblies
    private static List<string> Choose(string packageDirectory)
    {
        foreach (var kind in new[] { "lib", "ref" })
        {
            var directory = Path.Combine(packageDirectory, kind);
            if (!Directory.Exists(directory))
                continue;

            var candidates = Directory.GetDirectories(directory)
                .Select(path => (Path: path, Framework: NuGetFramework.Parse(Path.GetFileName(path))))
                .Where(item => !item.Framework.IsUnsupported)
                .ToList();

            if (candidates.Count == 0)
                continue;

            var reducer = new FrameworkReducer();
            var nearest = reducer.GetNearest(TargetFramework.NuGet, candidates.Select(item => item.Framework));
            if (nearest is null)
                continue;

            var chosen = candidates.FirstOrDefault(item => item.Framework.Equals(nearest));
            if (chosen.Path is null)
                continue;

            var files = Directory.EnumerateFiles(chosen.Path, "*.dll").ToList();
            if (files.Count > 0)
                return files;
        }

        return [];
    }

    private static string GlobalPackagesFolder()
    {
        var settings = Settings.LoadDefaultSettings(null);
        var folder = SettingsUtility.GetGlobalPackagesFolder(settings);
        return string.IsNullOrEmpty(folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages")
            : folder;
    }

    //Copies the package analyzers along with everything else so compilation never has to look back into the package cache
    private static int CopyAnalyzers(string packageDirectory, string id, string target)
    {
        var source = Path.Combine(packageDirectory, "analyzers");
        if (!Directory.Exists(source))
            return 0;

        var destination = Path.Combine(target, id, "analyzers");
        var copied = 0;
        foreach (var path in Directory.EnumerateFiles(source, "*.dll", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(destination, Path.GetRelativePath(source, path));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(path, copy, overwrite: true);
                copied++;
            }
            catch (IOException e)
            {
                Trace.Log($"cannot copy {path}: {e.Message}");
            }
        }

        return copied;
    }

    //Package assemblies in the restore directory; only the top level of each package is scanned so analyzers are not pulled in as references
    public static IReadOnlyList<string> Assemblies(string root)
    {
        var directory = Path.Combine(root, ProjectLayout.Packages);
        if (!Directory.Exists(directory))
            return [];

        var paths = new List<string>();
        foreach (var package in Directory.EnumerateDirectories(directory))
            paths.AddRange(Directory.EnumerateFiles(package, "*.dll", SearchOption.TopDirectoryOnly));
        return paths;
    }

    //Analyzer assemblies from every package; the UI x:Name fields and InitializeComponent come from them and would otherwise be missing
    public static IReadOnlyList<string> Analyzers(string root)
    {
        var directory = Path.Combine(root, ProjectLayout.Packages);
        if (!Directory.Exists(directory))
            return [];

        var paths = new List<string>();
        foreach (var package in Directory.EnumerateDirectories(directory))
        {
            var analyzers = Path.Combine(package, "analyzers");
            if (Directory.Exists(analyzers))
                paths.AddRange(Directory.EnumerateFiles(analyzers, "*.dll", SearchOption.AllDirectories));
        }
        return paths;
    }
}
