using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NacreCraft.ModBuild.Core;

//The mod project currently in scope; membership is decided solely by ncmod.json and the icon text uses the manifest name, falling back to id
//The manifest is read with Newtonsoft rather than System.Text.Json for one reason: it is the only json file a person writes by
//hand, and its reader carries line and column on every token, so a broken file can be reported where it broke
public sealed class ModProject
{
    //Manifest file name; both the loader and the tools recognize only this one
    public const string ManifestName = "ncmod.json";

    //Conventional icon file name used when the manifest omits icon
    public const string DefaultIconName = "icon.png";

    //Wording a reader throws when a hand written file goes wrong, paired with what actually has to change
    //The library names the character it choked on, which is precise but never says what to do about it
    private static readonly (string Piece, string Hint)[] CommonCauses =
    [
        ("Unexpected character encountered while parsing value: }",
            "a value is missing before the closing brace, or a trailing comma was left behind"),
        ("Unexpected character encountered while parsing value: ]",
            "a value is missing before the closing bracket, or a trailing comma was left behind"),
        ("Unexpected character encountered while parsing value: '",
            "json strings use double quotes, not single ones"),
        ("After parsing a value an unexpected character was encountered",
            "a comma is likely missing between two entries"),
        ("Unexpected end when reading JSON",
            "a brace or a bracket is left open"),
        ("Unexpected end when deserializing",
            "a value is cut short"),
        ("Invalid property identifier character",
            "a property name is not quoted"),
    ];

    //Last message printed, so a manifest that cannot be read does not repeat itself on every lookup in one run
    private static string _reported = string.Empty;

    private ModProject(string manifestPath, JObject document, string displayName, string id, string version,
        string iconPath, string namespaceName)
    {
        ManifestPath = manifestPath;
        Document = document;
        DisplayName = displayName;
        Id = id;
        Version = version;
        IconPath = iconPath;
        Namespace = namespaceName;
    }

    public string ManifestPath { get; }

    //The manifest as written, for the parts of the tools that read their own sections out of it
    public JObject Document { get; }

    public string DisplayName { get; }

    //Mod id from the manifest, exposed as $(ModId) in task interpolation
    public string Id { get; }

    //Version from the manifest, exposed as $(ModVersion) in task interpolation
    public string Version { get; }

    //Full icon file path taken from the manifest's icon field
    public string IconPath { get; }

    //Namespace derived from the manifest entry and used to fill placeholders in template drafts; an entry like NacreCraft.Test1.ModEntry drops its last segment and empty means it cannot be derived
    public string Namespace { get; }

    //Walk upward from the given directory looking for a mod project; running in a subdirectory still finds the root and only being fully outside counts as missing
    public static ModProject? TryFind(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var manifest = Path.Combine(current.FullName, ManifestName);
            if (File.Exists(manifest))
            {
                Trace.Log($"found mod manifest {manifest}");
                return Read(manifest, current.FullName);
            }
        }

        Trace.Log($"no {ManifestName} from {directory} upward, not inside a mod project");
        return null;
    }

    //Report points at a manifest that is there but cannot be used, printing once so the reason is not lost in silence
    //A file with a typo used to look like no mod project at all, which sent the reader looking in the wrong place
    public static void Report(string path, Exception error)
    {
        var line = (error as JsonReaderException)?.LineNumber ?? 0;
        var column = (error as JsonReaderException)?.LinePosition ?? 0;
        var where = line > 0 ? $":{line}:{column}" : string.Empty;
        var message = $"error: {path}{where} cannot be read: {Explain(error.Message)}";
        if (_reported == message)
            return;

        _reported = message;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    //Read name, id and icon from the manifest; missing identity counts as no project since exiting quietly beats dumping a stack
    private static ModProject? Read(string manifestPath, string directory)
    {
        JObject document;
        try
        {
            document = JObject.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Report(manifestPath, e);
            return null;
        }

        var name = Text(document, "name");
        var id = Text(document, "id");
        var displayName = string.IsNullOrWhiteSpace(name) ? id : name;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            Trace.Log($"manifest {manifestPath} has neither name nor id, treating it as no project");
            return null;
        }

        var iconName = Text(document, "icon");
        return new ModProject(
            manifestPath,
            document,
            displayName,
            id ?? string.Empty,
            Text(document, "version") ?? string.Empty,
            Path.GetFullPath(Path.Combine(directory,
                string.IsNullOrWhiteSpace(iconName) ? DefaultIconName : iconName)),
            NamespaceOf(Text(document, "entry")));
    }

    //Add an icon field when the manifest lacks one and report whether the file changed; an existing non-empty value is left alone
    public bool EnsureIcon(string iconFileName)
        => Edit(manifest =>
        {
            var existing = Text(manifest, "icon");
            if (!string.IsNullOrWhiteSpace(existing))
                return false;

            manifest["icon"] = iconFileName;
            return true;
        });

    //Load the manifest as an editable object and write it back only when the callback returns true
    public bool Edit(Func<JObject, bool> change)
    {
        JObject manifest;
        try
        {
            manifest = JObject.Parse(File.ReadAllText(ManifestPath));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Report(ManifestPath, e);
            return false;
        }

        if (!change(manifest))
            return false;

        Write(ManifestPath, manifest);
        return true;
    }

    //Write lays a manifest down the way a person would type it, keeping the file readable in a diff
    public static void Write(string path, JObject document)
        => File.WriteAllText(path, document.ToString(Formatting.Indented) + Environment.NewLine);

    //Add the usual cause behind the reader's wording, since it names the character but never the fix
    private static string Explain(string message)
    {
        foreach (var (piece, hint) in CommonCauses)
        {
            if (message.Contains(piece, StringComparison.Ordinal))
                return $"{message} ({hint})";
        }

        return message;
    }

    //Derive the namespace from the entry, returning empty when only a class name is present
    private static string NamespaceOf(string? entry)
    {
        var lastDot = entry?.LastIndexOf('.') ?? -1;
        return lastDot > 0 ? entry![..lastDot] : string.Empty;
    }

    //Read a string field, returning null when it is missing or of another type
    private static string? Text(JObject document, string name)
        => document.TryGetValue(name, out var value) && value.Type == JTokenType.String
            ? value.Value<string>()
            : null;
}
