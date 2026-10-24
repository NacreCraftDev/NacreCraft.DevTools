using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//KernelStore the kernel reference assemblies in a project
//They come from the same place as the server copy, reusing the same cache and download, just placed in the project
//A project that points at a local kernel directory takes them from there instead, and is read again on every build
internal static class KernelStore
{
    //Sync makes sure the kernel references are complete and reports whether they are usable
    //The direct build can only compile against a complete kernel, a single missing assembly makes the whole source report missing types
    public static bool Sync(string root, NcProject? project)
    {
        var destination = Path.Combine(root, ProjectLayout.Kernel);
        ServerStore.Configure(project);

        var complete = HasAssemblies(destination);

        //With the update off the kernel is taken exactly as the project holds it: nothing is downloaded and nothing
        //is copied over it, so a build keeps working against a kernel that was placed here by hand
        if (ServerStore.SkipUpdate)
        {
            if (complete)
            {
                Trace.Log($"kernel update off, using {destination} as it is");
                return true;
            }

            Console.WriteLine($"error: kernel update is off and {destination} holds no complete kernel");
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

            Console.WriteLine($"error: no kernel assembly found in {ServerStore.Root}");
            return false;
        }

        //The mod api lives in the loader assembly, so the compile reference must include it
        //A project that declares it does not use the api skips it, keeping that assembly out of a pure mod
        Launcher.SyncKernel(ServerStore.Root, destination, includeModLoader: true,
            includeModApi: project?.Build.DependsOnModApi ?? NcBuild.DefaultDependsOnModApi);
        if (HasAssemblies(destination))
            return true;

        Console.WriteLine($"error: no kernel assembly found in {ServerStore.Root}");
        return false;
    }

    //HasAssemblies whether the kernel assemblies in a directory are complete
    //The root assembly NacreCraft.dll counts on its own and holds the settings and launch argument types
    //Only the dotted sub assemblies decide completeness, so an old directory is detected and synced again
    private static bool HasAssemblies(string directory)
        => Directory.Exists(directory)
            && File.Exists(Path.Combine(directory, "NacreCraft.dll"))
            && Directory.EnumerateFiles(directory, "NacreCraft.*.dll").Any();
}
