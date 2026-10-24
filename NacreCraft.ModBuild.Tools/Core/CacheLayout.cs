namespace NacreCraft.ModBuild.Core;

//CacheLayout ncm's own directories under the user folder
//living there keeps them stable across upgrades and reinstalls and shared between installed projects
internal static class CacheLayout
{
    //Product folder under the user folder, falling back to the program root when unavailable so downloads still have a home
    private static string Home { get; } = Resolve();

    //Root of everything ncm keeps for itself, relocatable so a test run or a shared machine can point it elsewhere
    public static string Root { get; } = ResolveRoot();

    //ResolveRoot takes the environment override when one is given, the usual user folder otherwise
    private static string ResolveRoot()
    {
        var custom = Environment.GetEnvironmentVariable("NCM_CACHE_ROOT");
        return string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Home, "ncm")
            : Path.GetFullPath(custom);
    }

    //Packages and scripts from self update, kept apart from the disposable caches
    public static string Update { get; } = Path.Combine(Root, "Update");

    //Installed plugins, one folder per plugin holding its dlls
    public static string Tool { get; } = Path.Combine(Root, "Tool");

    //Vanilla client jar
    public static string Client => Path.Combine(Root, "Client");

    //Files the client and the server share, downloaded once and used by both sides
    public static string Shared => Path.Combine(Root, "Shared");

    //Files only the client build carries, they replace the shared copy when the client runs
    public static string ClientRuntime => Path.Combine(Root, "ClientRuntime");

    //Merged runtime the launcher reads, one folder per side: shared first, then that side's own files over it
    public static string Runtime => Path.Combine(Root, "Runtime");

    //Vanilla resource objects the client jar cannot supply, shared by every project
    public static string Assets => Path.Combine(Root, "Assets");

    //Server runtime and kernel, holding only what differs from the shared files
    public static string Server => Path.Combine(Root, "Server");

    //Template catalog and example files
    public static string Template => Path.Combine(Root, "Template");

    //Packages that miss the global nuget cache
    public static string Packages => Path.Combine(Root, "packages");

    //Compute the product folder; LocalApplicationData is empty in some environments, so fall back to the program root then
    private static string Resolve()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local) ? AppContext.BaseDirectory : Path.Combine(local, "NacreCraft");
    }
}
