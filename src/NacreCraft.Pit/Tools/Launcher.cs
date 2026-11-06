using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using NacreCraft.Pit.Core;

namespace NacreCraft.Pit.Tools;

//What a launch brings up: the layer assembly to load, the entry type inside it and the mod environment to bootstrap
//ArgsFile is the argument file the entry merges next to the program root
internal readonly record struct LaunchTarget(string Assembly, string Entry, string Environment, string ArgsFile)
{
    //The dedicated server
    public static readonly LaunchTarget Server =
        new("NacreCraft.Server", "NacreCraft.Game.ServerMain", "Server", "Server.Args");

    //The client
    public static readonly LaunchTarget Client =
        new("NacreCraft.Client", "NacreCraft.Client.ClientMain", "Client", "Client.Args");
}

//Launcher loads a kernel layer dynamically and runs it
//The sequence follows the NacreCraft.Loader entry: arm the early crash guard, register the kernel resolver, point the
//program root at the run folder, merge the side's argument file, bootstrap the mods, then hand the command line to the
//layer. Everything is resolved by name, since there are no compile-time kernel references; both roles share one run
//folder, so switching keeps the staged mods and the assets
internal static class Launcher
{
    //RunDirectoryName is the run folder holding saves, logs and config
    internal const string RunDirectoryName = "run";

    //RunFolderVariable carries this run's folder across the kernel's restart
    //The restart copies the environment but not where the command was issued, and by then the working directory is
    //already inside run, so deriving the folder from it again would nest one level deeper
    private const string RunFolderVariable = "PIT_RUN_FOLDER";

    //RunDirectory is this run's folder, holding the mods and saves
    //It sits under the working directory the command was issued from, except on a restart where it is inherited
    internal static string RunDirectory
    {
        get
        {
            var inherited = Environment.GetEnvironmentVariable(RunFolderVariable);
            return string.IsNullOrEmpty(inherited)
                ? Path.Combine(Environment.CurrentDirectory, RunDirectoryName)
                : inherited;
        }
    }

    //LaunchedVariable marks a process the kernel restarted, which finds everything the first one prepared
    private const string LaunchedVariable = "PIT_LAUNCHED";

    //Launched reports whether this process took over from one that already prepared the run
    //Repeating the command would not only build and stage again: the native layer is mapped into this process by now,
    //so a refresh would be asked to replace the very library it is running from
    internal static bool Launched => Environment.GetEnvironmentVariable(LaunchedVariable) == "1";

    //NativePathVariable the environment variable the kernel reads to find its native layer
    private const string NativePathVariable = "NC_NATIVE_PATH";

    //AssemblySearchPathVariable the environment variable the module rewriter reads to find referenced assemblies
    //The same name HookEngine declares; this tool does not reference that library, so the spelling is repeated here
    private const string AssemblySearchPathVariable = "NC_ASSEMBLY_SEARCH_PATH";

    //NativeLibraryNames the native layer file names per platform, the same list the kernel looks for
    private static readonly string[] NativeLibraryNames =
        ["nacrecraft_native.dll", "libnacrecraft_native.so", "libnacrecraft_native.dylib"];

    private const string ModLoaderAssemblyName = "NacreCraft.ModLoader";

    //BootAssemblyName the boot layer carrying the resolver, the crash guard and the program root
    private const string BootAssemblyName = "NacreCraft.Boot";

    //ModApiAssemblyName the mod api assembly name
    private const string ModApiAssemblyName = "NacreCraft.ModApi";

    //Launch prepares the run folder, syncs the kernel and starts the requested layer through its own entry point
    //source is the runtime directory of the side being launched, which the shared files are already merged into
    public static int Launch(string[] args, LaunchTarget target, string source)
    {
        var run = RunDirectory;
        Directory.CreateDirectory(run);

        //Named before the kernel is loaded, since it is the kernel that restarts the process and what it passes on is
        //the environment
        Environment.SetEnvironmentVariable(RunFolderVariable, run);
        Environment.SetEnvironmentVariable(LaunchedVariable, "1");

        //The kernel has to live under the program root to be found, and since the root later points at run the kernel must move with it
        SyncKernel(source, Path.Combine(run, ProjectLayout.KernelFolder));

        //Registered before a single assembly is loaded, because the kernel touches its third-party dependencies the moment the
        //program root is set: pointing the root at run makes it open its log directory and archive what is already there, and
        //that archive goes through a compressor assembly sitting beside the cache rather than under run
        //The callback hands every name starting with NacreCraft to the kernel, which owns those and runs them through the mod
        //rewriter, so this only answers for the libraries around them
        RegisterResolver(source, target);

        //Silk.NET resolves its window and Vulkan libraries by bare name, so the runtime's native folder has to be on the
        //loader's search order rather than beside the executable
        PointNativeSearchPath(source);

        Directory.SetCurrentDirectory(run);

        //The native layer has to be named before the kernel comes up
        //It is only ever loaded as a profiler, from a path fixed at process start, and the kernel looks for it beside the
        //program, which under a run is pit itself rather than the cache it was downloaded into
        PointNativeLayer(source);

        //Names the runtime directory for the module rewriter: the kernel holds only NacreCraft's own assemblies, so the
        //third-party packages a rewritten assembly references have to be found where the launcher reads them from
        //Appended rather than overwritten, so a directory named by the caller is not dropped
        var namedSearchPath = Environment.GetEnvironmentVariable(AssemblySearchPathVariable);
        Environment.SetEnvironmentVariable(AssemblySearchPathVariable,
            string.IsNullOrEmpty(namedSearchPath) ? source : namedSearchPath + Path.PathSeparator + source);

        //The boot layer carries the resolver and the program root; it sits beside the executable, so it is loaded before
        //anything reads AppPaths or resolves a sub-library, and the log directory follows the run folder from then on
        var boot = Load(KernelPath(source, BootAssemblyName + ".dll"));
        boot.GetType("NacreCraft.EmbeddedAssemblyLoader", throwOnError: true)!
            .GetMethod("Initialize", Type.EmptyTypes)!.Invoke(null, null);
        //The program root setter logs through the log sink, and the sink can only be found once the root points at the run
        //folder; preloading it breaks that circle, and the loader never injects the sink anyway
        LoadLogging(run, source);
        //the root is pointed at the run folder before anything else resolves, since the kernel directory hangs off it
        boot.GetType("NacreCraft.AppPaths", throwOnError: true)!
            .GetMethod("SetOverride")!.Invoke(null, new object?[] { run });
        //armed after the root, since its first line logs through the same sink
        boot.GetType("NacreCraft.Boot.EarlyCrashGuard", throwOnError: true)!
            .GetMethod("Install", Type.EmptyTypes)!.Invoke(null, null);

        var startupArgs = MergeArgsFile(args, run, target);

        //The log sink must be enabled before mod bootstrap, since the injection and kernel Initialize come after it and debug output would otherwise be lost
        EnableDebug(startupArgs);

        //Mod bootstrap must run before any layer type is resolved
        //The entry points take the problems back instead of drawing them, so the failure window can wait for the language
        //table; a console run prints them and goes on, which is where the mod author is already looking
        var loader = Load(KernelPath(source, ModLoaderAssemblyName + ".dll"));
        var bootstrap = loader.GetType("NacreCraft.ModLoader.ModBootstrap", throwOnError: true)!;
        var environment = loader.GetType("NacreCraft.ModLoader.ModEnvironment", throwOnError: true)!;
        var result = bootstrap.GetMethod("Run")!.Invoke(null,
            new object?[] { Enum.Parse(environment, target.Environment), null });
        ReportProblems(result);

        //The layer goes through the kernel resolver, which the bootstrap already preloaded as a rewritten assembly when injection applies
        //Loading it by path again would produce a second copy with mismatched types and undo that injection
        var layer = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(target.Assembly));
        layer.GetType(target.Entry, throwOnError: true)!
            .GetMethod("Run")!.Invoke(null, new object?[] { startupArgs });
        return 0;
    }

    //LoadLogging preloads the log sink from the kernel folder, which the program root setter and the crash guard both log through
    //The copy in the source directory is the fallback for a layout that keeps it beside the executable instead
    private static void LoadLogging(string run, string source)
    {
        var path = Path.Combine(run, ProjectLayout.KernelFolder, "NacreCraft.Logging.dll");
        if (!File.Exists(path))
            path = KernelPath(source, "NacreCraft.Logging.dll");

        try
        {
            AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        catch (Exception e)
        {
            Trace.Log($"cannot preload the log sink from {path}: {e.GetType().Name}: {e.Message}");
        }
    }

    //MergeArgsFile merges the side's argument file next to the program root, the same step every entry point performs
    //The run folder is the program root by the time this runs, so the file is the one a user dropped there
    private static string[] MergeArgsFile(string[] args, string run, LaunchTarget target)
    {
        try
        {
            var main = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("NacreCraft"));
            var merge = main.GetType("NacreCraft.LaunchArgsFile", throwOnError: false)?.GetMethod("Merge");
            if (merge?.Invoke(null, new object?[] { args, Path.Combine(run, target.ArgsFile) }) is string[] merged)
                return merged;
        }
        catch (Exception e)
        {
            Trace.Log($"cannot merge {target.ArgsFile}: {e.GetType().Name}: {e.Message}");
        }

        return args;
    }

    //ReportProblems prints what the mod bootstrap collected, which the entry points hand to the failure window
    private static void ReportProblems(object? result)
    {
        var problems = result?.GetType().GetProperty("Problems")?.GetValue(result);
        if (problems is not IEnumerable<string> lines)
            return;

        foreach (var line in lines)
            Console.Error.WriteLine($"warning: {line}");
    }

    //PointNativeSearchPath registers the runtime's native folder with the loader
    //Silk.NET looks for its libraries by bare name, and the path resolver it ships never consults the runtime: it tries
    //the program root and then the name alone. Under a .NET host the name search covers the default directories rather
    //than PATH, so the folder has to be added with AddDllDirectory, which only takes part in that order
    private static void PointNativeSearchPath(string source)
    {
        var directory = Path.Combine(source, "runtimes",
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native");
        if (!Directory.Exists(directory))
        {
            //Named here because the failure surfaces much later as "no suitable window platform", which says nothing about
            //where the runtime was looked for
            Console.Error.WriteLine($"warning: no native folder under {directory}");
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            //Unix keeps the search path in the environment, which a running process can still change
            var libraries = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? string.Empty;
            Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", directory + Path.PathSeparator + libraries);
            return;
        }

        //The default directories are re-asserted because AddDllDirectory is ignored until they are in effect
        SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs);
        if (AddDllDirectory(directory) == IntPtr.Zero)
            Console.Error.WriteLine($"warning: cannot register native folder {directory}: {Marshal.GetLastWin32Error()}");
        else
            Trace.Log($"native folder registered {directory}");

        //The probed library is the one the window platform needs; a failure here names the reason the platform is missing
        ProbeNative(directory, "glfw3.dll");
    }

    //ProbeNative loads one library by full path to surface the real error
    //A missing window platform reports nothing about the underlying failure, while this distinguishes a wrong architecture
    //or an unmet dependency from a library that simply was not found
    private static void ProbeNative(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"warning: {path} does not exist");
            return;
        }

        try
        {
            NativeLibrary.Load(path);
            Trace.Log($"native probe loaded {path}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"warning: cannot load {path}: {e.GetType().Name}: {e.Message}");
        }
    }

    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    //EnableDebug turns on the global debug mode and log levels for --debug
    //Same purpose as the entry points, except the types are not available at compile time so the two embedded libraries are loaded by name
    private static void EnableDebug(string[] args)
    {
        if (Array.IndexOf(args, "--debug") < 0)
            return;

        var config = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("NacreCraft.Config"));
        var logging = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("NacreCraft.Logging"));

        config.GetType("NacreCraft.Config.DebugMode", throwOnError: true)!
            .GetProperty("IsEnabled")!.SetValue(null, true);

        var log = logging.GetType("NacreCraft.Logging.Log", throwOnError: true)!;
        var level = Enum.Parse(logging.GetType("NacreCraft.Logging.LogLevel", throwOnError: true)!, "Debug");
        log.GetMethod("SetConsoleLevel")!.Invoke(null, new[] { level });
        log.GetMethod("SetFileLevel")!.Invoke(null, new[] { level });
        log.GetMethod("SetVerbose")!.Invoke(null, new object?[] { true });
    }

    //PointNativeLayer tells the kernel where the native layer sits
    //The kernel looks for it beside the program and under the program's own runtimes folder, and under a run both of
    //those point at pit rather than the cache the library was downloaded into, so the cached copy is named explicitly
    //An explicit setting from the caller wins, that is the kernel's own escape hatch
    private static void PointNativeLayer(string source)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(NativePathVariable)))
            return;

        var directory = Path.Combine(source, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        foreach (var name in NativeLibraryNames)
        {
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate))
                continue;

            Environment.SetEnvironmentVariable(NativePathVariable, candidate);
            Trace.Log($"native layer pointed at {candidate}");
            return;
        }

        Trace.Log($"no native layer under {directory}");
    }

    //SyncKernel copies the kernel assemblies into the target directory
    //Files matching on name and size are skipped, keeping later runs cheap
    //Assemblies the source no longer ships are removed, since a build that drops one would otherwise keep loading the copy
    //left here from the version before
    //includeModLoader adds the loader for the project's compile references, which need the mod interfaces it carries
    //The run folder must not have it, since the main library and loader are already loaded here
    //includeModApi adds the mod api for projects built against it, a pure mod leaves it out
    //The root assembly NacreCraft.dll also carries types those compile references need
    //extraSource fills in the assemblies the main source does not ship, which is how the client half reaches a
    //project build: a file both sides carry is kept from the main source, since only one copy can be referenced
    internal static void SyncKernel(string source, string destination, bool includeModLoader = false,
        bool includeModApi = true, string? extraSource = null)
    {
        Directory.CreateDirectory(destination);

        //The main library travels with the sub-libraries: nothing loads it by path anymore, so the layer reaches it through
        //the kernel resolver like every other one
        const string pattern = "NacreCraft*.dll";
        //names this sync is responsible for, which is what tells a leftover apart from something else in the folder
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copied = 0;
        foreach (var path in KernelFiles(source, pattern))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!includeModLoader && name == ModLoaderAssemblyName)
                continue;
            if (!includeModApi && name == ModApiAssemblyName)
                continue;

            expected.Add(Path.GetFileName(path));

            var target = Path.Combine(destination, Path.GetFileName(path));
            var existing = new FileInfo(target);
            var origin = new FileInfo(path);
            //Treat a file as unchanged only when both size and write time match
            //Size alone would miss an edit that keeps the length, and such a file would never sync again
            if (existing.Exists && existing.Length == origin.Length
                && existing.LastWriteTimeUtc == origin.LastWriteTimeUtc)
                continue;

            File.Copy(path, target, overwrite: true);
            copied++;
        }

        if (extraSource is not null)
            copied += Fill(extraSource, destination, pattern, includeModLoader, includeModApi, expected);

        var removed = Remove(destination, expected);

        Trace.Log($"kernel synced {copied} file(s), removed {removed} from {destination}");
    }

    //Fill adds the assemblies only the extra source ships, leaving every file the main source already placed alone
    //Their names join expected so a later sync does not mistake them for leftovers of an older kernel
    private static int Fill(string source, string destination, string pattern, bool includeModLoader,
        bool includeModApi, HashSet<string> expected)
    {
        var filled = 0;
        foreach (var path in KernelFiles(source, pattern))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!includeModLoader && name == ModLoaderAssemblyName)
                continue;
            if (!includeModApi && name == ModApiAssemblyName)
                continue;

            expected.Add(Path.GetFileName(path));

            var target = Path.Combine(destination, Path.GetFileName(path));
            if (File.Exists(target))
                continue;

            File.Copy(path, target, overwrite: false);
            filled++;
        }

        return filled;
    }

    //Remove drops the kernel assemblies the source does not ship anymore
    //Only names the kernel itself uses are considered, so anything else sitting in the folder is left alone
    //A server that is still running holds its own copies, so a file that refuses to go is reported rather than thrown
    private static int Remove(string destination, HashSet<string> expected)
    {
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(destination, "NacreCraft*.dll"))
        {
            if (expected.Contains(Path.GetFileName(path)))
                continue;

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine(
                    $"warning: cannot remove the outdated kernel assembly {Path.GetFileName(path)}: {e.Message}");
            }
        }

        return removed;
    }

    //KernelFiles lists the kernel assemblies of a source directory
    //A build output keeps NacreCraft.dll and the loader at its root and the dotted assemblies in a kernel folder beside
    //them, while a downloaded cache is flat, so both spots are read and the flat one wins on a name that appears twice
    private static IEnumerable<string> KernelFiles(string source, string pattern)
    {
        //a side that was never cached has no directory, which is an empty set rather than a failure
        if (!Directory.Exists(source))
            yield break;

        foreach (var path in Directory.EnumerateFiles(source, pattern))
            yield return path;

        var nested = Path.Combine(source, ProjectLayout.KernelFolder);
        if (!Directory.Exists(nested))
            yield break;

        foreach (var path in Directory.EnumerateFiles(nested, pattern))
            yield return path;
    }

    //KernelPath locates one kernel assembly, which a build output keeps either beside its root or in the kernel folder next to it
    //A missing file keeps the plain path so the load failure names where it was looked for
    private static string KernelPath(string source, string name)
    {
        var direct = Path.Combine(source, name);
        if (File.Exists(direct))
            return direct;

        var nested = Path.Combine(source, ProjectLayout.KernelFolder, name);
        if (File.Exists(nested))
            return nested;

        return ServerStore.FindKernelFile(name) ?? Path.Combine(source, name);
    }

    //RegisterResolver handles everything that is not a kernel assembly, which the kernel resolves itself
    //Loading kernel assemblies here would run before the mod rewriter and make the injection rules too late
    private static void RegisterResolver(string source, LaunchTarget target)
    {
        //The deps file holds the runtime graph, and a build output names it after its own executable instead, so
        //without one the plain directory lookup has to carry every dependency
        var deps = Path.ChangeExtension(Path.Combine(source, target.Assembly), ".deps.json");
        var resolver = File.Exists(deps)
            ? new AssemblyDependencyResolver(Path.Combine(source, target.Assembly + ".dll"))
            : null;

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name is null || name.Name.StartsWith("NacreCraft", StringComparison.Ordinal))
                return null;

            var path = resolver?.ResolveAssemblyToPath(name);
            if (path is null)
            {
                //Fall back to the directory for assemblies the deps file does not list
                var candidate = Path.Combine(source, name.Name + ".dll");
                path = File.Exists(candidate) ? candidate : null;
            }
            return path is null ? null : context.LoadFromAssemblyPath(path);
        };

        //Native libraries work the same way, taking the registered location or leaving it to the system
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += (_, name) =>
        {
            var path = resolver?.ResolveUnmanagedDllToPath(name) ?? NativeProbe(source, name);
            return path is null ? IntPtr.Zero : NativeLibrary.Load(path);
        };
    }

    //NativeProbe looks for a native library where a build output keeps it
    //The runtime folder a deps file would have pointed at holds it while the plain directory does for a few of them, and
    //since the request carries a bare library name the platform extension has to be tried as well
    private static string? NativeProbe(string source, string name)
    {
        var native = Path.Combine(source, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        string[] directories = [native, source];
        string[] extensions = ["", ".dll", ".so", ".dylib"];

        foreach (var directory in directories)
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }

        return null;
    }

    //Load loads an assembly by path; there is no fallback here, failure throws
    private static Assembly Load(string path)
    {
        Trace.Log($"loading assembly {path}");
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
}
