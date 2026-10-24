using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace NacreCraft.ModBuild.Core;

//One entry from an index
//An empty hash means the index carries only paths, in which case the file's presence is all that matters
internal readonly record struct BundleFile(string Path, string Hash);

//RuntimeBundle one downloadable runtime folder: an index, a cache directory and a base address
//A release is split into a shared bundle every side needs and one bundle per side holding what differs, so this class
//only knows about its own folder and leaves the ordering between them to RuntimeStore
internal sealed class RuntimeBundle
{
    //Retry limit per file
    private const int Attempts = 5;

    //Separator between hash and path in the index, two spaces to match sha256sum output
    private const string HashSeparator = "  ";

    private readonly string label;
    private readonly string defaultBase;
    private readonly string defaultIndex;

    private string source;
    private string format = NcSource.DefaultFormat;
    private Regex? matcher;

    public RuntimeBundle(string label, string cacheRoot, string defaultBase, string defaultIndex = NcSource.DefaultIndex)
    {
        this.label = label;
        this.defaultBase = defaultBase;
        this.defaultIndex = defaultIndex;
        this.source = defaultBase;
        CacheRoot = cacheRoot;
        IndexFileName = defaultIndex;
    }

    //Name the bundle is reported under, which is also the folder name on the release side
    public string Label => label;

    //Directory ncm downloads into
    public string CacheRoot { get; private set; }

    public string IndexFileName { get; private set; }

    public string IndexPath => Path.Combine(CacheRoot, IndexFileName);

    //Hash of the index currently on disk, used to tell whether the merged runtime is up to date
    public string Stamp
    {
        get
        {
            try
            {
                return File.Exists(IndexPath)
                    ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(IndexPath)))
                    : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }

    //MoveCache points the bundle somewhere else, which the project config can ask for the server runtime
    public void MoveCache(string directory) => CacheRoot = directory;

    //Reset returns the bundle to the built-in address and rules
    //A project without a source must not inherit the one the previous project named
    public void Reset()
    {
        source = defaultBase;
        IndexFileName = defaultIndex;
        format = NcSource.DefaultFormat;
        matcher = null;
    }

    //Configure swaps address and parsing rules together, keeping the defaults for anything unset
    public void Configure(string? url, string index, string format, string pattern)
    {
        Reset();

        if (!string.IsNullOrWhiteSpace(url))
            source = url.EndsWith('/') ? url : url + "/";

        IndexFileName = string.IsNullOrWhiteSpace(index) ? defaultIndex : index;
        this.format = string.IsNullOrWhiteSpace(format) ? NcSource.DefaultFormat : format;

        if (!string.Equals(this.format, "regex", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            matcher = new Regex(pattern, RegexOptions.Compiled);
        }
        catch (ArgumentException e)
        {
            Console.WriteLine($"error: Source Pattern \"{pattern}\" does not compile: {e.Message}");
        }
    }

    //Ensure makes sure the cached files are complete, downloading only what is missing
    public bool Ensure()
    {
        var entries = ReadIndex();
        if (entries.Count == 0)
        {
            Console.WriteLine($"{label} cache is empty, downloading {IndexFileName}");
            if (!TryDownload(IndexFileName, out var indexError))
            {
                Console.WriteLine($"error: failed to download {Url(IndexFileName)}: {indexError}");
                return false;
            }

            entries = ReadIndex();
        }

        if (entries.Count == 0)
        {
            Console.WriteLine($"error: {IndexPath} lists no file");
            return false;
        }

        var missing = new List<BundleFile>();
        foreach (var entry in entries)
        {
            if (!IsForHost(entry.Path))
                continue;

            var target = ResolveCachePath(entry.Path);
            if (target is null || !File.Exists(target))
                missing.Add(entry);
        }

        if (missing.Count == 0)
        {
            Trace.Log($"{label} cache hit {CacheRoot} ({RuntimeInformation.RuntimeIdentifier})");
            return true;
        }

        Console.WriteLine($"Downloading {missing.Count} {label} file(s) to {CacheRoot}");
        return Download(missing);
    }

    //Refresh fetches the remote index over the local one and fills in files that differ by hash
    public bool Refresh()
    {
        Console.WriteLine($"Refreshing {label} cache from {Url(IndexFileName)}");
        if (!TryDownload(IndexFileName, out var error))
        {
            Console.WriteLine($"error: failed to download {Url(IndexFileName)}: {error}");
            return false;
        }

        var entries = ReadIndex();
        if (entries.Count == 0)
        {
            Console.WriteLine($"error: {IndexPath} lists no file");
            return false;
        }

        var stale = new List<BundleFile>();
        foreach (var entry in entries)
        {
            if (!IsForHost(entry.Path))
                continue;

            var target = ResolveCachePath(entry.Path);
            if (target is null || !File.Exists(target) || !HashMatches(target, entry.Hash))
                stale.Add(entry);
        }

        if (stale.Count == 0)
        {
            Console.WriteLine($"{label} cache is up to date");
            return true;
        }

        Console.WriteLine($"Downloading {stale.Count} changed {label} file(s) to {CacheRoot}");
        return Download(stale);
    }

    //HasCompleteCache whether every index entry for this host is already on disk, without hashing anything
    public bool HasCompleteCache()
    {
        var entries = ReadIndex();
        if (entries.Count == 0)
            return false;

        foreach (var entry in entries)
        {
            if (!IsForHost(entry.Path))
                continue;

            var target = ResolveCachePath(entry.Path);
            if (target is null || !File.Exists(target))
                return false;
        }

        return true;
    }

    //Entries the index of this bundle lists
    public List<BundleFile> Entries() => ReadIndex();

    //Whether this index entry belongs to the host running now
    public bool IsForHost(string entry)
    {
        var segments = entry.Split('/');
        if (segments.Length >= 2 && segments[0] == "runtimes")
            return MatchesRuntime(segments[1]);

        var architecture = RuntimeInformation.ProcessArchitecture;
        return segments[0] switch
        {
            "amd64" => architecture == Architecture.X64,
            "x86" => architecture == Architecture.X86,
            "arm64" => architecture == Architecture.Arm64,
            "arm" => architecture == Architecture.Arm,
            _ => true,
        };
    }

    //Resolve an index path inside the cache directory, returning null when it escapes
    public string? ResolveCachePath(string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(CacheRoot, normalized));
        var root = Path.GetFullPath(CacheRoot) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    //Download every entry, returning false as soon as one fails
    private bool Download(List<BundleFile> files)
    {
        if (!Transfer.Enabled)
        {
            if (DownloadCore(files, null, out var failure))
                return true;

            Console.WriteLine($"error: {failure}");
            return false;
        }

        var ok = false;
        var error = string.Empty;
        AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn())
            .Start(context =>
            {
                var task = context.AddTask($"downloading {label} files", maxValue: files.Count);
                ok = DownloadCore(files, name =>
                {
                    task.Description = $"[green]{Markup.Escape(name)}[/]";
                    task.Increment(1);
                }, out error);
            });

        if (ok)
            return true;

        Console.WriteLine($"error: {error}");
        return false;
    }

    //Download every entry and stop at the first failure
    //Without a progress bar every file is announced on its own line before it starts, which is what a pipe reader sees
    private bool DownloadCore(List<BundleFile> files, Action<string>? completed, out string error)
    {
        error = string.Empty;
        for (var index = 0; index < files.Count; index++)
        {
            var entry = files[index];
            if (completed is null)
                Console.WriteLine($"Downloading {label} files {index + 1}/{files.Count}: {entry.Path}");

            if (TryDownload(entry.Path, out var failure))
            {
                Trace.Log($"{label} file {index + 1}/{files.Count} done {entry.Path}");
                completed?.Invoke(entry.Path);
                continue;
            }

            error = $"failed to download {entry.Path} after {Attempts} attempt(s): {failure}";
            return false;
        }
        return true;
    }

    //Whether a platform directory name matches this machine
    private static bool MatchesRuntime(string rid)
    {
        var host = RuntimeInformation.RuntimeIdentifier;
        if (string.Equals(rid, host, StringComparison.OrdinalIgnoreCase))
            return true;

        var separator = host.IndexOf('-');
        if (separator < 0)
            return false;

        var system = host[..separator];
        var architecture = host[(separator + 1)..];
        return string.Equals(rid, system, StringComparison.OrdinalIgnoreCase)
            || (rid.StartsWith(system + "-", StringComparison.OrdinalIgnoreCase)
                && rid.EndsWith("-" + architecture, StringComparison.OrdinalIgnoreCase));
    }

    //Whether the sha256 of a local file matches the index entry
    private static bool HashMatches(string path, string expected)
    {
        if (expected.Length == 0)
            return true;

        try
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException e)
        {
            Trace.Log($"cannot hash {path}: {e.Message}");
            return false;
        }
    }

    //Read the index, returning an empty list when it is absent or unreadable
    private List<BundleFile> ReadIndex()
    {
        if (!File.Exists(IndexPath))
            return new List<BundleFile>();

        try
        {
            return File.ReadAllLines(IndexPath)
                .Select(ParseEntry)
                .Where(entry => entry.Path.Length > 0)
                .ToList();
        }
        catch (IOException e)
        {
            Trace.Log($"cannot read {IndexPath}: {e.Message}");
            return new List<BundleFile>();
        }
    }

    //Split one index line according to the configured format
    private BundleFile ParseEntry(string line)
    {
        var text = line.Trim();
        if (text.Length == 0)
            return new BundleFile(string.Empty, string.Empty);

        if (string.Equals(format, "regex", StringComparison.OrdinalIgnoreCase) && matcher is not null)
        {
            var match = matcher.Match(text);
            return match.Success && match.Groups.Count >= 3
                ? new BundleFile(Normalize(match.Groups[2].Value), match.Groups[1].Value)
                : new BundleFile(string.Empty, string.Empty);
        }

        //The plain format gives only a path and treats the hash as absent, so presence alone decides
        if (string.Equals(format, "plain", StringComparison.OrdinalIgnoreCase))
            return new BundleFile(Normalize(text), string.Empty);

        var separator = text.IndexOf(HashSeparator, StringComparison.Ordinal);
        if (separator > 0 && IsHex(text.AsSpan(0, separator)))
            return new BundleFile(Normalize(text[(separator + HashSeparator.Length)..]), text[..separator]);

        return new BundleFile(Normalize(text), string.Empty);
    }

    //Normalize index paths to slash separators
    private static string Normalize(string path)
        => path.Trim().Replace('\\', '/');

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
            return false;

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
                return false;
        }
        return true;
    }

    //Download one file, retrying up to the limit
    private bool TryDownload(string relative, out string error)
    {
        var target = ResolveCachePath(relative);
        if (target is null)
        {
            error = $"path escapes the cache directory: {relative}";
            return false;
        }

        error = string.Empty;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            if (Transfer.DownloadToFile(Url(relative), target, static (_, _) => { }, out error))
                return true;

            Trace.Log($"download {label}/{relative} attempt {attempt} failed: {error}");
        }
        return false;
    }

    //Build the download address from a relative index path, escaping each segment so spaces cannot break the URL
    private string Url(string relative)
        => source + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
}
