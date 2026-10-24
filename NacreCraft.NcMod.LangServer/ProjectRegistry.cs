using NacreCraft.ModBuild.Core;

namespace NacreCraft.NcMod.LangServer;

//ProjectRegistry tracks which ncproj projects a session has opened
//Loading only registers roots and their project references; building a compilation stays under completion.build
internal sealed class ProjectRegistry
{
    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);

    //Roots currently being loaded, which catches reference cycles
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);

    //Loaded lists the project roots the session has opened
    public IReadOnlyCollection<string> Loaded => _loaded;

    public bool IsLoaded(string root)
        => _loaded.Contains(Path.GetFullPath(root));

    //Load registers one project and every project its references need
    public void Load(string root)
    {
        var config = Find(root);
        if (config is null)
            return;

        var directory = Path.GetDirectoryName(config.Path);
        if (directory is null || !_loading.Add(directory))
            return;

        try
        {
            if (!_loaded.Add(directory))
                return;

            foreach (var include in config.ProjectReferences)
            {
                var referenced = Referenced(Path.GetFullPath(Path.Combine(directory, include)));
                if (referenced is not null)
                    Load(referenced);
            }
        }
        finally
        {
            _loading.Remove(directory);
        }
    }

    //LoadAll registers every project under a workspace, which the load setting can ask for
    public void LoadAll(string workspace)
    {
        foreach (var root in Discover(workspace))
            Load(root);
    }

    //Discover lists every ncproj directory under a workspace, skipping build output
    public static IReadOnlyList<string> Discover(string workspace)
    {
        var roots = new List<string>();
        if (Directory.Exists(workspace))
            Walk(workspace, roots);

        return roots;
    }

    private static void Walk(string directory, List<string> roots)
    {
        try
        {
            if (IsOutput(directory))
                return;

            foreach (var config in Directory.EnumerateFiles(directory, "*" + NcProject.Extension))
            {
                var root = Path.GetDirectoryName(Path.GetFullPath(config));
                if (root is not null)
                    roots.Add(root);
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
                Walk(child, roots);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static bool IsOutput(string directory)
    {
        var name = Path.GetFileName(directory);
        return name is "bin" or "obj" or "Build" or ".git" or "node_modules" or ".vs";
    }

    //Referenced turns a project reference into the directory that actually holds an ncproj
    private static string? Referenced(string path)
    {
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory);

        if (string.IsNullOrEmpty(directory))
            return null;

        var config = Find(directory);
        return config is null ? null : Path.GetDirectoryName(config.Path);
    }

    //Find resolves the config of a directory without throwing when a folder is missing
    private static NcProject? Find(string? directory)
    {
        while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory);

        if (string.IsNullOrEmpty(directory))
            return null;

        try
        {
            return NcProject.TryFind(directory, out _);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
