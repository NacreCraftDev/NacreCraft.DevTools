namespace NacreCraft.ModBuild.Core;

//Local cache of template assets under the user cache; the catalog only hits the network when missing locally and example files are cache-first too
public static class TemplateStore
{
    public const string CatalogFileName = "NacreCraftTemplate.yaml";

    //Built-in catalog bootstrap URL used only when no local catalog exists
    private const string DefaultCatalogUrl =
        "https://raw.githubusercontent.com/NacreCraftDev/NacreCraftTemplate/refs/heads/main/NacreCraftTemplate.yaml";

    //Catalog URL in use, which project config can override; the base inside the catalog is the example baseline and is a separate thing
    private static string CatalogUrl { get; set; } = DefaultCatalogUrl;

    //Override for the example baseline that replaces the catalog's base when set
    private static string BaseOverride { get; set; } = string.Empty;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    //Whether this run wants the latest; setting it bypasses stale CDN copies, and the static value is fixed when the tool starts since one command runs per process
    public static bool Refresh { get; set; }

    //Cache directory under the user folder, unaffected by ncm version changes
    public static string Root => CacheLayout.Template;

    public static string CatalogPath => Path.Combine(Root, CatalogFileName);

    //Adjust sources from project config, keeping defaults for anything unset; mirror prefixes are prepended verbatim and ncm adds no processing
    public static void Configure(NcProject? project)
    {
        var template = project?.Template;
        if (template is null)
            return;

        if (!string.IsNullOrWhiteSpace(template.Url))
            CatalogUrl = template.Url;

        BaseOverride = template.Base;
    }

    //The catalog already parsed, kept across checks since the editor reads it on every save
    private static TemplateCatalog? _catalog;
    private static string _catalogStamp = string.Empty;
    private static string _catalogBase = string.Empty;

    //Read the catalog, downloading one first when the local copy is absent
    public static TemplateCatalog? LoadCatalog()
    {
        if (!File.Exists(CatalogPath))
        {
            //the editor switch that keeps the kernel off the network covers this download too
            if (ServerStore.SkipUpdate)
            {
                Trace.Log("template catalog is missing and updates are off, the mod api check is skipped");
                return null;
            }

            Console.WriteLine($"Template cache is empty, downloading {CatalogFileName}");
            if (!TryDownload(CatalogUrl, CatalogPath, out var error))
            {
                Console.WriteLine($"Failed to download {CatalogUrl}: {error}");
                return null;
            }
        }
        else
        {
            Trace.Log($"catalog cache hit {CatalogPath}");
        }

        //parsing the whole api table is only worth it when the file changed, a check reads it again and again
        var stamp = Stamp(CatalogPath);
        var catalog = _catalog;
        if (catalog is null || _catalogStamp != stamp)
        {
            catalog = TemplateCatalog.Read(CatalogPath);
            if (catalog is not null)
            {
                _catalog = catalog;
                _catalogStamp = stamp;
                _catalogBase = catalog.Base;
            }
        }

        //the cached instance carries the base of the last caller, so it is put back to what the file said first
        if (catalog is not null)
            catalog.Base = string.IsNullOrWhiteSpace(BaseOverride) ? _catalogBase : BaseOverride;

        return catalog;
    }

    //Stamp identifies the file state the parsed catalog was made from
    private static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    //Fetch a template file preferring the local copy; returns the on-disk path, or null with error set on failure
    public static string? FetchFile(string url, string relative, out string error)
    {
        var target = ResolveCachePath(relative);
        if (target is null)
        {
            error = $"template path escapes the cache directory: {relative}";
            return null;
        }

        if (File.Exists(target))
        {
            error = string.Empty;
            return target;
        }

        return TryDownload(url, target, out error) ? target : null;
    }

    //Map a catalog-relative path inside the cache, returning null when it escapes; the catalog comes from the network so .. must be blocked to keep writes contained
    private static string? ResolveCachePath(string relative)
    {
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(Root, normalized));
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    //Download to a path through a temp file moved in place so partial content never stays in the cache
    private static bool TryDownload(string url, string path, out string error)
    {
        try
        {
            var bytes = Http.GetByteArrayAsync(Refresh ? FreshUrl(url) : url).GetAwaiter().GetResult();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
            error = string.Empty;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    //Swap in a URL that bypasses the stale CDN copy when the latest is required
    //raw.githubusercontent.com honors neither no-cache nor query parameters, while the github.com raw path redirects to a temporary-token URL that is always fresh
    //Non-GitHub raw URLs pass through untouched since their caching is not ours to manage
    private static string FreshUrl(string url)
    {
        const string host = "https://raw.githubusercontent.com/";
        if (!url.StartsWith(host, StringComparison.OrdinalIgnoreCase))
            return url;

        var segments = url[host.Length..].Split('/');
        if (segments.Length < 3)
            return url;

        var tail = string.Join('/', segments[2..]);
        const string heads = "refs/heads/";
        if (tail.StartsWith(heads, StringComparison.Ordinal))
            tail = tail[heads.Length..];

        return $"https://github.com/{segments[0]}/{segments[1]}/raw/{tail}";
    }
}
