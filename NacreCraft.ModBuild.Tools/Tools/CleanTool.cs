using NacreCraft.ModBuild.Core;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Tools;

//CleanTool removes the build cache
//Inside a project it only clears Build, elsewhere only the shared download cache exists and removal asks first
//A named target clears that one cache wherever the command is typed and asks nothing
internal static class CleanTool
{
    //AllOption also clears the download cache inside a project and skips the question outside one
    private const string AllOption = "--all";

    //Register this tool with its name, description and parameters
    public static void Register()
        => ToolRegistry.Register("clean", "Remove the project build cache, or the shared download cache when there is no project here", Run,
        [
            new(AllOption, "Remove everything ncm downloaded without asking: the client jar, the asset objects, the server runtime, the template files, the packages and the update package"),
            new("<cache>", $"Remove one downloaded cache on its own, wherever the command is typed: {CacheNames()}"),
        ]);

    //Run clears the project copy inside a project, or the shared copy outside one after asking
    //Everything removed is re-fetched on the next build, so the download cache is left alone inside a project unless a target names it
    private static int Run(string[] args)
    {
        var project = NcProject.TryFind(Environment.CurrentDirectory, out var configError);
        if (!string.IsNullOrEmpty(configError))
        {
            Console.WriteLine($"error: {configError}");
            return 1;
        }

        var caches = Cached(project);
        var all = false;
        var wanted = new List<string>();

        foreach (var arg in args)
        {
            if (arg == AllOption)
            {
                all = true;
                continue;
            }

            if (caches.Any(cache => cache.Name == arg))
            {
                wanted.Add(arg);
                continue;
            }

            Console.WriteLine($"error: unknown clean argument {arg}");
            var close = Similarity.Closest(arg, caches.Select(cache => cache.Name));
            if (close is not null)
                Console.WriteLine($"Did you mean {close}?");

            Console.WriteLine($"Usage: ncm clean [{AllOption}] [<cache>...]");
            return 1;
        }

        var freed = 0L;

        //A named target is an explicit request, so it is honored wherever the command is typed and never prompts
        //--all already covers every cache, so naming targets alongside it changes nothing
        if (wanted.Count > 0 && !all)
        {
            freed += caches.Where(cache => wanted.Contains(cache.Name)).Sum(cache => Remove(cache.Directory, cache.What));

            Console.WriteLine($"Freed {freed / 1024.0 / 1024.0:F1} MB");
            return 0;
        }

        if (project is not null)
        {
            freed += Remove(Path.Combine(project.Directory, ProjectLayout.Build), "build cache");

            if (all)
                freed += RemoveDownloads(caches);
        }
        //Outside a project only the shared cache can be cleared, ask before touching it
        else if (all || AskDownloads(caches))
        {
            freed += RemoveDownloads(caches);
        }
        else
        {
            Console.WriteLine("Nothing removed");
        }

        Console.WriteLine($"Freed {freed / 1024.0 / 1024.0:F1} MB");
        return 0;
    }

    //AskDownloads asks whether to clear the shared cache, showing its location and size
    //With redirected input nobody can answer, so it only explains what to do instead of deleting
    private static bool AskDownloads(CacheTarget[] caches)
    {
        var present = caches.Where(cache => Directory.Exists(cache.Directory)).ToList();
        if (present.Count == 0)
        {
            Console.WriteLine($"No project here and everything ncm downloaded under {CacheLayout.Root} is already gone");
            return false;
        }

        var size = present.Sum(cache => Size(cache.Directory)) / 1024.0 / 1024.0;
        if (Console.IsInputRedirected)
        {
            Console.WriteLine($"No project here, what ncm downloaded under {CacheLayout.Root} takes {size:F1} MB, pass {AllOption} to remove it");
            return false;
        }

        Console.WriteLine($"No project here, what ncm downloaded under {CacheLayout.Root} takes {size:F1} MB");
        return Prompt.Confirm("Remove it?", defaultYes: true, warn: true);
    }

    //RemoveDownloads clears every cache ncm can lay down again
    //Installed plugins are not cache: nothing can fetch them back, so they are left alone
    private static long RemoveDownloads(CacheTarget[] caches)
        => caches.Sum(cache => Remove(cache.Directory, cache.What));

    //Cached the directories ncm can rebuild, together with the name they are picked by and the name they are reported under
    //The server cache is taken wherever the project points it, which is the default location unless overridden
    //A local kernel directory is not a cache: it is the project's own copy, so it is never removed here
    private static CacheTarget[] Cached(NcProject? project)
    {
        ServerStore.Configure(project);
        return
        [
            new("client", CacheLayout.Client, "client cache"),
            new("assets", CacheLayout.Assets, "asset object cache"),
            new("shared", CacheLayout.Shared, "shared runtime cache"),
            new("server", ServerStore.CacheRoot, "server runtime cache"),
            new("clientruntime", CacheLayout.ClientRuntime, "client runtime cache"),
            new("runtime", CacheLayout.Runtime, "merged runtime cache"),
            new("template", CacheLayout.Template, "template cache"),
            new("packages", CacheLayout.Packages, "package cache"),
            new("update", CacheLayout.Update, "update cache"),
        ];
    }

    //CacheNames lists the targets accepted on the command line, read from the same table that resolves their locations
    private static string CacheNames() => string.Join(", ", Cached(null).Select(cache => cache.Name));

    //Remove deletes one directory and returns the freed bytes
    private static long Remove(string directory, string what)
    {
        if (!Directory.Exists(directory))
            return 0;

        var freed = Size(directory);
        try
        {
            Delete(directory);
            Console.WriteLine($"Removed {what} at {directory}");
            return freed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"error: cannot remove {directory}: {e.Message}");
            Console.ResetColor();
            return 0;
        }
    }

    //Delete removes a tree, clearing the read-only flag a copied package file may carry
    //A file the compiler still has mapped keeps the directory in place, which the caller reports instead of crashing
    private static void Delete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (UnauthorizedAccessException)
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(directory, recursive: true);
        }
    }

    //Size the directory footprint in bytes
    private static long Size(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    //CacheTarget one downloaded cache: the name it is picked by, where it sits for this project and how it is reported
    private readonly record struct CacheTarget(string Name, string Directory, string What);
}
