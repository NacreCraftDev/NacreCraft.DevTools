namespace NacreCraft.ModBuild.Core;

//ServerStore the server half of the runtime, kept as the entry point the build and the run tools already use
//What is downloaded is a pair of bundles now: the shared files every side carries and the server files that differ from them
public static class ServerStore
{
    //Environment switch that keeps a build off the network
    //A release pipeline has to compile the mod api before the download source carries an index, and the index is
    //built from that same mod api, so it sets this to run against the kernel already on disk instead of waiting
    private const string SkipUpdateVariable = "NCM_SKIP_KERNEL_UPDATE";

    //Whether the process was started with the update switched off, which an editor setting must not turn back on
    public static bool SkipUpdateFromEnvironment { get; } =
        Environment.GetEnvironmentVariable(SkipUpdateVariable) is { Length: > 0 } flag && flag != "0";

    //Whether the kernel is only read from what is already on disk, with no download and no index refresh
    //A host embedding this library can switch it at runtime, which is what the editor setting does
    public static bool SkipUpdate { get; set; } = SkipUpdateFromEnvironment;

    //Local kernel directory from the project config, taken in place of the cache, null when unset
    private static string? local;

    //Merged runtime built for this configuration, null until Ensure or Refresh ran
    private static string? prepared;

    //Whether the kernel is read from a local directory rather than the cache
    public static bool UsesLocalDirectory => local is not null;

    //Directory ncm downloads the server files into, overridden by the Server section of the project config when present
    public static string CacheRoot => RuntimeStore.Server.CacheRoot;

    //Directory the kernel is read from: the merged runtime once one was built, which shared files come first in
    public static string Root => local ?? prepared ?? CacheRoot;

    public static string IndexFileName => RuntimeStore.Server.IndexFileName;

    public static string IndexPath => RuntimeStore.Server.IndexPath;

    //Locate a kernel file, looking in the runtime and in every cache the merged one is built from
    //A server build puts NacreCraft.dll at its output root and the dotted assemblies in a kernel folder next to it, while a
    //downloaded cache keeps everything flat, so both places are searched and the flat one is found first
    public static string? FindKernelFile(string name)
    {
        foreach (var directory in new[]
                 {
                     Root, CacheLayout.Shared, RuntimeStore.Server.CacheRoot, CacheLayout.ClientRuntime,
                 })
        {
            var direct = Path.Combine(directory, name);
            if (File.Exists(direct))
                return direct;

            var nested = Path.Combine(directory, ProjectLayout.KernelFolder, name);
            if (File.Exists(nested))
                return nested;
        }

        return null;
    }

    //Adjust the cache location and source from the project config, keeping defaults for anything unset
    //Cache resolves against the project root, and the source applies to all three bundles, since the server address
    //names the folder the shared and client ones sit beside
    public static void Configure(NcProject? project)
    {
        local = null;
        prepared = null;
        RuntimeStore.Configure(project?.Sources);

        if (project is null)
            return;

        if (!string.IsNullOrWhiteSpace(project.Server.Cache))
            RuntimeStore.Server.MoveCache(Path.GetFullPath(Path.Combine(project.Directory, project.Server.Cache)));

        var source = project.Sources;
        if (source is null || string.IsNullOrWhiteSpace(source.Directory))
            return;

        //A local directory stands in for the cache and nothing is downloaded into it
        //A project that names one which is not there falls through to the url, which is the fallback
        var directory = Path.GetFullPath(Path.Combine(project.Directory, source.Directory));
        if (Directory.Exists(directory))
        {
            local = directory;
            return;
        }

        Console.WriteLine($"warning: <Sources Directory=\"{source.Directory}\"> does not exist, the source is used instead");
    }

    //Make sure the cached files are complete and return whether the runtime is usable
    //The shared files are fetched first, then only the server ones that differ from them
    public static bool Ensure()
    {
        //A local directory is the kernel itself, with nothing to fill in
        if (local is not null)
        {
            Trace.Log($"server kernel taken from local directory {local}");
            prepared = local;
            return true;
        }

        //With the update off the cache is only read, never filled in, which is what lets a build run while the
        //source carries no index yet; an incomplete cache reports and leaves the project's own kernel in place
        if (SkipUpdate)
        {
            var usable = RuntimeStore.Shared.HasCompleteCache() && RuntimeStore.Server.HasCompleteCache();
            Trace.Log($"kernel update off ({SkipUpdateVariable}), cache usable: {usable}");
            if (usable)
                prepared = RuntimeStore.Prepare(RuntimeSide.Server);
            return usable;
        }

        if (!RuntimeStore.Ensure(RuntimeSide.Server))
            return false;

        prepared = RuntimeStore.Prepare(RuntimeSide.Server);
        return true;
    }

    //Fetch the remote indexes over the local ones and fill in files that differ by hash
    public static bool Refresh()
    {
        //The local directory is never downloaded into, so there is nothing to compare against an index
        if (local is not null)
        {
            Console.WriteLine($"Kernel comes from the local directory {local}, nothing to refresh");
            return true;
        }

        if (SkipUpdate)
        {
            Console.WriteLine($"Kernel update is off ({SkipUpdateVariable}), nothing to refresh");
            return true;
        }

        if (!RuntimeStore.Refresh(RuntimeSide.Server))
            return false;

        prepared = RuntimeStore.Prepare(RuntimeSide.Server);
        return true;
    }
}
