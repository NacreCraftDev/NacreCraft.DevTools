using System.Security.Cryptography;
using System.Text.Json;
using Spectre.Console;

namespace NacreCraft.ModBuild.Core;

//One entry of the resource index
//Path is the resource pack path such as minecraft/sounds/mob/stray/death2.ogg and Hash its content address
internal readonly record struct AssetObject(string Path, string Hash, long Size);

//Local cache of the vanilla resource objects the client jar cannot supply
//The jar carries no pack.mcmeta, only en_us of the language tables, and none of the sounds at all, so they come from
//the resource index the launcher downloads alongside the version; one copy serves every project on the machine
public static class AssetsStore
{
    //Official host of the resource objects, addressed as <host>/<hash prefix>/<hash>
    private const string OfficialHost = "https://resources.download.minecraft.net";

    //Mirror carrying the same layout, since direct access is slow in some regions
    private const string MirrorHost = "https://bmclapi2.bangbang93.com/assets";

    //Mirror of the index itself, which it serves by id rather than by the official path
    private const string MirrorIndexHost = "https://bmclapi2.bangbang93.com/assets";

    //Index prefix of the sounds, the rest of the path being what the kernel's sound lookup expects
    private const string SoundPrefix = "minecraft/sounds/";

    //Objects fetched at the same time, enough to hide the round trip without hammering the mirror
    private const int Concurrency = 8;

    //Retry limit per address
    private const int Attempts = 3;

    //Cache directory shared by every project
    public static string Root => CacheLayout.Assets;

    //Index id the cache was built from, empty until Ensure runs
    public static string IndexId { get; private set; } = string.Empty;

    //Sound source handed to the kernel as --sounds-dir, valid once EnsureSounds has returned it
    public static string SoundsRoot => Path.Combine(Root, "sounds");

    private static string IndexesDir => Path.Combine(Root, "indexes");

    private static string ObjectsDir => Path.Combine(Root, "objects");

    //Ensure makes the index and every object it lists local and returns the index id, or null when impossible
    public static string? Ensure()
    {
        if (ClientStore.AssetIndex() is not { } index)
            return null;

        var indexFile = Path.Combine(IndexesDir, index.Id + ".json");
        if (!EnsureIndexFile(index, indexFile))
            return null;

        var entries = ReadIndex(indexFile);
        if (entries.Count == 0)
        {
            Console.WriteLine($"error: {indexFile} lists no object");
            return null;
        }

        IndexId = index.Id;

        var missing = entries.Where(entry => !HasObject(entry)).ToList();
        if (missing.Count == 0)
        {
            Trace.Log($"asset object cache hit {Root} ({entries.Count} object(s))");
            return index.Id;
        }

        Console.WriteLine($"Downloading {missing.Count} asset object(s) to {Root}");
        return Download(missing) ? index.Id : null;
    }

    //CachedIndex id of the index already on disk, which a restarted launch needs without going to the network
    //The kernel falls back to the newest index when the name is empty, so a missing one is not fatal
    public static string CachedIndex()
    {
        try
        {
            var latest = new DirectoryInfo(IndexesDir).GetFiles("*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            return latest is null ? string.Empty : Path.GetFileNameWithoutExtension(latest.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.Log($"cannot list the index cache: {e.Message}");
            return string.Empty;
        }
    }

    //EnsureSounds copies the sound objects out of the index as plain files and returns that directory
    //The kernel copies from a directory tree rather than reading objects itself, and the part after
    //minecraft/sounds is exactly the relative path its sound lookup expects
    public static string? EnsureSounds()
    {
        if (Ensure() is null)
            return null;

        var indexFile = Path.Combine(IndexesDir, IndexId + ".json");
        var entries = ReadIndex(indexFile)
            .Where(entry => entry.Path.StartsWith(SoundPrefix, StringComparison.Ordinal))
            .ToList();
        if (entries.Count == 0)
        {
            Console.WriteLine("warning: the asset index lists no sound, the client runs without audio");
            return null;
        }

        var copied = 0;
        foreach (var entry in entries)
        {
            var relative = entry.Path[SoundPrefix.Length..];
            var target = Path.Combine(SoundsRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target) && (entry.Size < 0 || new FileInfo(target).Length == entry.Size))
                continue;

            var source = ObjectPath(entry.Hash);
            if (!File.Exists(source))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            copied++;
        }

        Trace.Log($"sound files ready {SoundsRoot} ({entries.Count} entry, {copied} copied)");
        return SoundsRoot;
    }

    //EnsureIndexFile downloads the index when it is absent or its content does not match
    //Returns false without throwing when every address fails
    private static bool EnsureIndexFile(AssetIndexFile index, string target)
    {
        if (File.Exists(target) && (index.Size <= 0 || new FileInfo(target).Length == index.Size))
            return true;

        Console.WriteLine($"Downloading asset index {index.Id}");
        foreach (var url in IndexUrls(index))
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                if (!Transfer.DownloadToFile(url, target, static (_, _) => { }, out var error))
                {
                    Trace.Log($"asset index {url} attempt {attempt} failed: {error}");
                    continue;
                }

                if (index.Sha1.Length == 0 || string.Equals(Sha1(target), index.Sha1, StringComparison.OrdinalIgnoreCase))
                    return true;

                Trace.Log($"asset index {url} sha1 mismatch, trying the next source");
                Discard(target);
                break;
            }
        }

        Console.WriteLine($"error: failed to download the asset index {index.Id}");
        return false;
    }

    //Addresses to try for the index, mirror first and the address the version json carries second
    private static IEnumerable<string> IndexUrls(AssetIndexFile index)
    {
        yield return $"{MirrorIndexHost}/{index.Id}.json";
        if (index.Url.Length > 0)
            yield return index.Url;
    }

    //Read the objects list of an index file, returning an empty list when it cannot be read
    private static List<AssetObject> ReadIndex(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("objects", out var objects))
                return [];

            var entries = new List<AssetObject>();
            foreach (var property in objects.EnumerateObject())
            {
                if (!property.Value.TryGetProperty("hash", out var hashNode)
                    || hashNode.GetString() is not { Length: > 0 } hash)
                    continue;

                var size = property.Value.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : -1L;
                entries.Add(new AssetObject(property.Name, hash, size));
            }

            return entries;
        }
        catch (Exception e)
        {
            Console.WriteLine($"error: cannot read {path}: {e.Message}");
            return [];
        }
    }

    //Download every missing object, drawing a progress bar when the output is a terminal
    private static bool Download(List<AssetObject> entries)
    {
        if (!Transfer.Enabled)
            return Across(entries, Announced(entries.Count), null);

        var ok = false;
        AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn())
            .Start(context =>
            {
                var task = context.AddTask("[green]asset objects[/]", maxValue: entries.Count);
                ok = Across(entries, null, entry =>
                {
                    //the bar names the object that just landed, which is the file the reader is waiting on
                    task.Description = $"[green]{Markup.Escape(entry.Path)}[/]";
                    task.Increment(1);
                });
            });
        return ok;
    }

    //Announced prints one line per object as it starts, which is the only progress a pipe reader gets
    private static Action<AssetObject> Announced(int total)
    {
        var started = 0;
        return entry =>
        {
            var index = Interlocked.Increment(ref started);
            Console.WriteLine($"Downloading asset objects {index}/{total}: {entry.Path}");
        };
    }

    //Fetch the objects in parallel, counting the ones that never arrived
    private static bool Across(List<AssetObject> entries, Action<AssetObject>? started, Action<AssetObject>? completed)
    {
        var failed = 0;
        Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = Concurrency }, entry =>
        {
            started?.Invoke(entry);
            if (FetchObject(entry))
                completed?.Invoke(entry);
            else
                Interlocked.Increment(ref failed);
        });

        if (failed == 0)
            return true;

        Console.WriteLine($"error: {failed} asset object(s) could not be downloaded");
        return false;
    }

    //Fetch one object through the mirror first and the official host second, verifying the sha1 each time
    private static bool FetchObject(AssetObject entry)
    {
        var target = ObjectPath(entry.Hash);
        if (HasObject(entry))
            return true;

        var relative = entry.Hash[..2] + "/" + entry.Hash;
        foreach (var host in new[] { MirrorHost, OfficialHost })
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                if (!Transfer.DownloadToFile($"{host}/{relative}", target, static (_, _) => { }, out var error))
                {
                    Trace.Log($"asset object {entry.Hash} attempt {attempt} failed: {error}");
                    continue;
                }

                if (HasObject(entry))
                    return true;

                //Content does not match the index, so this host cannot serve it and the next one gets a turn
                Trace.Log($"asset object {entry.Hash} failed verification from {host}");
                Discard(target);
                break;
            }
        }

        return false;
    }

    //Whether the object is already local with the size the index declares
    private static bool HasObject(AssetObject entry)
        => File.Exists(ObjectPath(entry.Hash))
           && (entry.Size < 0 || new FileInfo(ObjectPath(entry.Hash)).Length == entry.Size);

    //Content addressed path of an object under the shared objects directory
    private static string ObjectPath(string hash)
        => Path.Combine(ObjectsDir, hash[..2], hash);

    //Lowercase hex sha1 of a file, empty when it cannot be read
    private static string Sha1(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA1.HashData(stream));
        }
        catch (IOException e)
        {
            Trace.Log($"cannot hash {path}: {e.Message}");
            return string.Empty;
        }
    }

    //Delete a file that failed validation
    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException e)
        {
            Trace.Log($"cannot remove {path}: {e.Message}");
        }
    }
}
