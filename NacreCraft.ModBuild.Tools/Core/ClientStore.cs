using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Spectre.Console;

namespace NacreCraft.ModBuild.Core;

//The client section of a version json
internal readonly record struct ClientJarFile(string Url, long Size, string Sha1);

//The asset index section of a version json, the id names the index file and the url downloads it
internal readonly record struct AssetIndexFile(string Id, string Url, string Sha1, long Size);

//Local cache of the vanilla client jar
//The version is read from the kernel SharedConstants and the jar stays in the user cache directory
//It is only needed to extract assets on the first launch and not afterwards
public static class ClientStore
{
    //Mirror mapped from several official domains because direct access is too slow in some regions
    private const string Mirror = "https://bmclapi2.bangbang93.com";

    //Mirror address of the version manifest
    private const string MirrorManifestUrl = Mirror + "/mc/game/version_manifest.json";

    //Official address of the version manifest, used when the mirror fails
    private const string OfficialManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest.json";

    //Retry limit per address
    private const int Attempts = 3;

    //Anything smaller than this is treated as a truncated download
    private const long MinJarSize = 1024L;

    //Official domain to mirror mapping used for both version json and jar addresses
    private static readonly (string From, string To)[] Mirrors =
    [
        ("https://piston-meta.mojang.com", Mirror),
        ("https://piston-data.mojang.com", Mirror),
        ("https://launcher.mojang.com", Mirror),
        ("https://launchermeta.mojang.com", Mirror),
    ];

    //Cache directory under the user profile, unaffected by ncm version changes
    public static string Root => CacheLayout.Client;

    //Jar version from the config, empty follows the kernel version
    private static string ConfiguredVersion { get; set; } = string.Empty;

    //Direct URL from the config, which skips the version manifest
    private static string ConfiguredJar { get; set; } = string.Empty;

    //Adjust the version and direct URL from the project config, keeping defaults for anything unset
    public static void Configure(NcProject? project)
    {
        if (project is null)
            return;

        ConfiguredVersion = project.Client.Version;
        ConfiguredJar = project.Client.Jar;
    }

    //Make sure the client jar this machine needs is cached and return its path, or null when impossible
    //An existing jar that looks complete avoids a fresh download on every launch
    public static string? Ensure()
    {
        var version = Version();
        if (version is null)
        {
            Console.WriteLine("error: cannot read the kernel version from NacreCraft.Config, pin one with <Client Version=\"...\">");
            return null;
        }

        var target = Path.Combine(Root, version, version + ".jar");
        var existing = new FileInfo(target);
        if (existing.Exists && existing.Length >= MinJarSize)
        {
            Trace.Log($"client jar cache hit {target}");
            return target;
        }

        Console.WriteLine($"Downloading Minecraft client jar {version} to {Path.GetDirectoryName(target)}");
        return Fetch(version, target) ? target : null;
    }

    //Read the asset index entry of the version json, which is what lists the objects the jar cannot supply
    //Returns null when the version cannot be resolved or the json carries no index, printing why
    internal static AssetIndexFile? AssetIndex()
    {
        var version = Version();
        if (version is null)
        {
            Console.WriteLine("error: cannot read the kernel version from NacreCraft.Config, pin one with <Client Version=\"...\">");
            return null;
        }

        var jsonUrl = FindVersionUrl(version);
        if (jsonUrl is null)
            return null;

        var json = TryGet(jsonUrl, out var error);
        if (json is null)
        {
            Console.WriteLine($"error: failed to download {jsonUrl}: {error}");
            return null;
        }

        using var document = TryParse(json);
        if (document is null
            || !document.RootElement.TryGetProperty("assetIndex", out var index)
            || !index.TryGetProperty("id", out var idNode)
            || idNode.GetString() is not { Length: > 0 } id)
        {
            Console.WriteLine($"error: version {version} has no asset index entry");
            return null;
        }

        var url = index.TryGetProperty("url", out var urlNode) ? urlNode.GetString() ?? string.Empty : string.Empty;
        var sha1 = index.TryGetProperty("sha1", out var sha1Node) ? sha1Node.GetString() ?? string.Empty : string.Empty;
        var size = index.TryGetProperty("size", out var sizeNode) ? sizeNode.GetInt64() : 0L;
        return new AssetIndexFile(id, url, sha1, size);
    }

    //Which version to use, preferring the configured one and reading the kernel only when unset
    private static string? Version()
        => string.IsNullOrWhiteSpace(ConfiguredVersion) ? KernelVersion() : ConfiguredVersion;

    //Version string from the kernel, whose part before the dash is the Minecraft version
    //This reads a compile-time constant via metadata only and unloads the load context afterwards
    private static string? KernelVersion()
    {
        var path = ServerStore.FindKernelFile("NacreCraft.Config.dll");
        if (path is null)
        {
            Trace.Log($"kernel config assembly not found under {ServerStore.Root}");
            return null;
        }

        try
        {
            var context = new AssemblyLoadContext("ncm-kernel-version", isCollectible: true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(path);
                var value = assembly.GetType("NacreCraft.Config.SharedConstants")
                    ?.GetField("Version")?.GetRawConstantValue() as string;
                if (string.IsNullOrEmpty(value))
                    return null;

                var separator = value.IndexOf('-');
                return separator < 0 ? value : value[..separator];
            }
            finally
            {
                context.Unload();
            }
        }
        catch (Exception e)
        {
            Trace.Log($"cannot read the kernel version: {e.GetType().Name}");
            return null;
        }
    }

    //Download the jar from the configured URL when present or via the version manifest otherwise
    //Everything checkable is verified before the file is kept, though size and hash only exist on the manifest path
    private static bool Fetch(string version, string target)
    {
        ClientJarFile? entry = null;
        string url;
        if (string.IsNullOrWhiteSpace(ConfiguredJar))
        {
            if (FindClient(version) is not { } client)
                return false;

            entry = client;
            url = client.Url;
        }
        else
        {
            url = ConfiguredJar;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";

        string error;
        var ok = Transfer.Enabled
            ? DownloadWithProgress(url, temp, version, out error)
            : TryDownload(url, temp, static (_, _) => { }, out error);
        if (!ok)
        {
            Console.WriteLine($"error: failed to download {url}: {error}");
            return false;
        }

        var info = new FileInfo(temp);
        var size = entry?.Size ?? 0L;
        if (size > 0 && info.Length != size)
        {
            Console.WriteLine($"error: client jar size mismatch, expected {size} got {info.Length}");
            Discard(temp);
            return false;
        }

        var sha1 = entry?.Sha1 ?? string.Empty;
        var actual = Sha1(temp);
        if (sha1.Length > 0 && !string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"error: client jar sha1 mismatch, expected {sha1} got {actual}");
            Discard(temp);
            return false;
        }

        //The direct URL path has no manifest so at least reject an obviously truncated download
        if (info.Length < MinJarSize)
        {
            Console.WriteLine($"error: the downloaded jar is only {info.Length} byte(s), treating it as incomplete");
            Discard(temp);
            return false;
        }

        File.Move(temp, target, overwrite: true);
        Trace.Log($"client jar saved {target}");
        return true;
    }

    //Download while drawing a byte progress bar
    //A server that omits the total length falls back to a spinner that reports the received bytes
    private static bool DownloadWithProgress(string url, string target, string label, out string error)
    {
        var ok = false;
        var failure = string.Empty;
        AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new TransferSpeedColumn())
            .Start(context =>
            {
                var task = context.AddTask($"[green]{Markup.Escape(label)}[/]");
                ok = TryDownload(url, target, (received, total) =>
                {
                    if (total is { } length and > 0)
                    {
                        task.MaxValue = length;
                        task.Value = received;
                        return;
                    }

                    task.IsIndeterminate = true;
                    task.Description = $"[green]{Markup.Escape(label)}[/] {Human(received)}";
                }, out failure);
            });

        error = failure;
        return ok;
    }

    //Stream a download through the mirror first and the original address second, retrying each a few times
    private static bool TryDownload(string url, string target, Action<long, long?> report, out string error)
    {
        error = string.Empty;
        foreach (var candidate in new[] { MirrorUrl(url), url }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                if (Transfer.DownloadToFile(candidate, target, report, out error))
                    return true;

                Trace.Log($"download {candidate} attempt {attempt} failed: {error}");
            }
        }
        return false;
    }

    //Compute the lowercase hex sha1 of a file, returning an empty string when it cannot be read
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

    //Format a byte count for humans, used on the progress bar when the total length is unknown
    private static string Human(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.0} MB"
            : $"{bytes / 1024d:0.0} KB";

    //Delete a temporary file that failed validation
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

    //FindVersionUrl looks a version up in the manifest and returns the address of its version json
    //The mirror answers with a truncated body from time to time, so a source whose payload does not parse is passed
    //over instead of ending the lookup, and the original address gets its turn
    private static string? FindVersionUrl(string version)
    {
        var error = string.Empty;
        foreach (var url in new[] { MirrorManifestUrl, OfficialManifestUrl })
        {
            var manifest = TryGet(url, out error);
            if (manifest is null)
                continue;

            using var document = TryParse(manifest);
            if (document is null)
            {
                Trace.Log($"manifest {url} did not parse, trying the next source");
                continue;
            }

            if (!document.RootElement.TryGetProperty("versions", out var versions))
            {
                Console.WriteLine("error: the version manifest has no version list");
                return null;
            }

            foreach (var item in versions.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || id.GetString() != version)
                    continue;

                if (item.TryGetProperty("url", out var address) && address.GetString() is { Length: > 0 } target)
                    return target;
                break;
            }

            Console.WriteLine($"error: version {version} is not listed in the manifest");
            return null;
        }

        Console.WriteLine($"error: failed to download the version manifest: {error}");
        return null;
    }

    //Look up a version in the manifest and read its client section from the version json
    private static ClientJarFile? FindClient(string version)
    {
        var jsonUrl = FindVersionUrl(version);
        if (jsonUrl is null)
            return null;

        var json = TryGet(jsonUrl, out var jsonError);
        if (json is null)
        {
            Console.WriteLine($"error: failed to download {jsonUrl}: {jsonError}");
            return null;
        }

        using var versionDocument = TryParse(json);
        if (versionDocument is null
            || !versionDocument.RootElement.TryGetProperty("downloads", out var downloads)
            || !downloads.TryGetProperty("client", out var client)
            || !client.TryGetProperty("url", out var clientUrl)
            || clientUrl.GetString() is not { Length: > 0 } address)
        {
            Console.WriteLine($"error: version {version} has no client download entry");
            return null;
        }

        var size = client.TryGetProperty("size", out var sizeValue) ? sizeValue.GetInt64() : 0L;
        var sha1 = client.TryGetProperty("sha1", out var sha1Value) ? sha1Value.GetString() ?? string.Empty : string.Empty;
        return new ClientJarFile(address, size, sha1);
    }

    //TryParse reads a downloaded json payload, treating a truncated or malformed body as a failed download
    private static JsonDocument? TryParse(byte[] payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException e)
        {
            Trace.Log($"json payload did not parse: {e.Message}");
            return null;
        }
    }

    //Download a small payload through the mirror first and the original address second, retrying each a few times
    private static byte[]? TryGet(string url, out string error)
    {
        error = string.Empty;
        foreach (var candidate in new[] { MirrorUrl(url), url }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var bytes = Transfer.GetBytes(candidate, out error);
                if (bytes is not null)
                    return bytes;

                Trace.Log($"download {candidate} attempt {attempt} failed: {error}");
            }
        }
        return null;
    }

    //Rewrite an official address to the mirror, leaving addresses that are already mirrored or unrelated unchanged
    private static string MirrorUrl(string url)
    {
        foreach (var (from, to) in Mirrors)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return to + url[from.Length..];
        }
        return url;
    }
}
