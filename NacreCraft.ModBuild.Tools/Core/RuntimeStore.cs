namespace NacreCraft.ModBuild.Core;

using System.Runtime.InteropServices;

//RuntimeSide which half of the runtime a launch needs
internal enum RuntimeSide
{
    Client,
    Server,
}

//RuntimeStore the three bundles a release is split into and the merged folders the launcher reads
//The shared bundle holds the files both sides ship byte for byte, so a client and a server installation download it
//once; each side then only fetches what differs, and a launch gets shared files first with the side's own on top
internal static class RuntimeStore
{
    //Root of the release repository, the three bundle folders sit beside each other under it
    private const string ReleaseRoot = "https://raw.githubusercontent.com/NacreCraftDev/NacreCraft.Release/refs/heads/main/";

    //Files the client and the server carry identically
    public static RuntimeBundle Shared { get; } = new("shared", CacheLayout.Shared, ReleaseRoot + "shared/");

    public static RuntimeBundle Client { get; } = new("client", CacheLayout.ClientRuntime, ReleaseRoot + "client/");

    public static RuntimeBundle Server { get; } = new("server", CacheLayout.Server, ReleaseRoot + "server/");

    //The bundle one side adds over the shared files
    public static RuntimeBundle Of(RuntimeSide side)
        => side == RuntimeSide.Server ? Server : Client;

    //Configure applies the project's kernel source to all three bundles
    //The configured address names the server folder, and the other two are taken from the folders beside it
    public static void Configure(NcSource? source)
    {
        if (source is null)
        {
            Shared.Reset();
            Client.Reset();
            Server.Reset();
            return;
        }

        Shared.Configure(Sibling(source.Url, "shared"), source.Index, source.Format, source.Pattern);
        Client.Configure(Sibling(source.Url, "client"), source.Index, source.Format, source.Pattern);
        Server.Configure(source.Url, source.Index, source.Format, source.Pattern);
    }

    //Ensure downloads the shared files first, then the ones the side adds
    public static bool Ensure(RuntimeSide side)
        => Shared.Ensure() && Of(side).Ensure();

    //Refresh compares hashes for the shared files first, then for the side's own
    public static bool Refresh(RuntimeSide side)
        => Shared.Refresh() && Of(side).Refresh();

    //Directory the merged runtime of one side lives in
    public static string Destination(RuntimeSide side)
        => Path.Combine(CacheLayout.Runtime, side == RuntimeSide.Server ? "server" : "client");

    //Prepare builds the directory a launch reads: shared files first, then the side's own replacing them
    //The index hashes are kept beside the result, so an unchanged release is not merged again on every run
    public static string Prepare(RuntimeSide side)
    {
        var bundle = Of(side);
        var destination = Destination(side);

        var sharedStamp = Shared.Stamp;
        var sideStamp = bundle.Stamp;

        //Without an index there is nothing to merge, so the side cache is used as it is
        if (sharedStamp.Length == 0 || sideStamp.Length == 0)
        {
            Trace.Log($"{side} runtime has no index, using {bundle.CacheRoot}");
            return bundle.CacheRoot;
        }

        var stamp = sharedStamp + ":" + sideStamp;
        var stampFile = Path.Combine(destination, ".runtime");
        if (File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp)
            return destination;

        try
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);

            Directory.CreateDirectory(destination);

            var merged = Merge(destination, Shared);
            merged += Merge(destination, bundle);

            File.WriteAllText(stampFile, stamp);
            Trace.Log($"{side} runtime merged, {merged} file(s) at {destination}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            //a running client or server holds its assemblies, and the merged folder from before is still usable
            Console.WriteLine($"warning: cannot rebuild {destination}: {e.Message}");
        }

        return destination;
    }

    //Merge links every file of one bundle into the destination, replacing what a previous bundle put there
    private static int Merge(string destination, RuntimeBundle bundle)
    {
        var merged = 0;
        foreach (var entry in bundle.Entries())
        {
            if (!bundle.IsForHost(entry.Path))
                continue;

            var origin = bundle.ResolveCachePath(entry.Path);
            if (origin is null || !File.Exists(origin))
                continue;

            var target = Path.Combine(destination, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            Link(origin, target);
            merged++;
        }

        return merged;
    }

    //Link puts one file in place, hard linking when the volume allows it so the merged folder costs no extra space
    private static void Link(string origin, string target)
    {
        var parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        if (File.Exists(target))
            File.Delete(target);

        if (TryLink(origin, target))
            return;

        File.Copy(origin, target, overwrite: true);
    }

    //TryLink asks the system for a hard link, which keeps one copy of a shared file on disk
    //It fails across volumes and on file systems without links, where a plain copy is the fallback
    private static bool TryLink(string origin, string target)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? CreateHardLink(target, origin, IntPtr.Zero)
                : LinkUnix(origin, target) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", SetLastError = true, EntryPoint = "link")]
    private static extern int LinkUnix(string existingPath, string newPath);

    //Sibling replaces the last segment of a source address with another bundle folder
    //A configured address points at the server folder, and the shared and client folders sit beside it
    private static string? Sibling(string? url, string folder)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? null : trimmed[..(slash + 1)] + folder + "/";
    }
}
