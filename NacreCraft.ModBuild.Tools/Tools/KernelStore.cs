using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//KernelStore the kernel reference assemblies in a project
//They come from the same place as the server copy, reusing the same cache and download, just placed in the project
//A project that points at a local kernel directory takes them from there instead, and is read again on every build
internal static class KernelStore
{
    //Sync makes sure the kernel references are complete and reports whether they are usable
    //The direct build can only compile against a complete kernel, a single missing assembly makes the whole source report missing types
    //Both sides are placed, since a template project carries client code and only the client bundle ships the client assemblies
    public static bool Sync(string root, NcProject? project)
    {
        var destination = Path.Combine(root, ProjectLayout.Kernel);
        ServerStore.Configure(project);
        var includeModApi = project?.Build.DependsOnModApi ?? NcBuild.DefaultDependsOnModApi;

        var complete = HasAssemblies(destination);

        //With the update off the kernel is taken exactly as the project holds it: nothing is downloaded and nothing
        //is copied over it, so a build keeps working against a kernel that was placed here by hand
        if (ServerStore.SkipUpdate)
        {
            if (complete)
            {
                //the client side is still filled in from whatever the cache already holds, without going online
                Launcher.SyncKernel(ServerStore.Root, destination, includeModLoader: true, includeModApi: includeModApi,
                    extraSource: ClientSource(online: false));
                Trace.Log($"kernel update off, using {destination} as it is");
                return true;
            }

            Progress.WriteLine($"error: kernel update is off and {destination} holds no complete kernel");
            return false;
        }

        //The sync runs on every build rather than only when the destination looks empty: a kernel build drops assemblies
        //between versions, and the copies left behind would be the ones the compiler picks up
        //A local kernel directory is edited in place for the same reason, having no completeness to trust
        if (!ServerStore.Ensure())
        {
            //a cache that cannot be filled in leaves a kernel that is already there alone, so the build keeps its references
            if (complete)
                return true;

            Progress.WriteLine($"error: no kernel assembly found in {ServerStore.Root}");
            return false;
        }

        //The mod api lives in the loader assembly, so the compile reference must include it
        //A project that declares it does not use the api skips it, keeping that assembly out of a pure mod
        Launcher.SyncKernel(ServerStore.Root, destination, includeModLoader: true, includeModApi: includeModApi,
            extraSource: ClientSource(online: true));
        if (HasAssemblies(destination))
            return true;

        Progress.WriteLine($"error: no kernel assembly found in {ServerStore.Root}");
        return false;
    }

    //ClientSource is the directory the client kernel assemblies are read from
    //A server build alone does not carry them, so the client half is fetched and merged through the same caches
    //Going online is skipped when the update is off, and a client that cannot be fetched only costs those assemblies
    private static string? ClientSource(bool online)
    {
        if (online && !RuntimeStore.Ensure(RuntimeSide.Client))
        {
            Progress.WriteLine("warning: the client kernel is unavailable, the build only sees the server assemblies");
            return null;
        }

        var directory = RuntimeStore.Prepare(RuntimeSide.Client);
        if (Directory.Exists(directory))
            return directory;

        Trace.Log($"client kernel not cached under {directory}");
        return null;
    }

    //HasAssemblies whether the kernel assemblies in a directory are complete
    //The root assembly NacreCraft.dll counts on its own and holds the settings and launch argument types
    //Only the dotted sub assemblies decide completeness, so an old directory is detected and synced again
    private static bool HasAssemblies(string directory)
        => Directory.Exists(directory)
            && File.Exists(Path.Combine(directory, "NacreCraft.dll"))
            && Directory.EnumerateFiles(directory, "NacreCraft.*.dll").Any();
}
