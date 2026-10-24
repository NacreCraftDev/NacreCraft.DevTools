using System.Xml;
using System.Xml.Linq;

namespace NacreCraft.ModBuild.Core;

//One scalar setting the project config understands
//Element and Attribute name where it lives, Kind how it is edited, Default what the build assumes when it is absent
//Required settings may be changed but never removed, since the file would stop describing a project
public sealed class NcConfigField
{
    public NcConfigField(string key, string element, string attribute, string kind, string @default,
        bool required, params string[] options)
    {
        Key = key;
        Element = element;
        Attribute = attribute;
        Kind = kind;
        Default = @default;
        Required = required;
        Options = options;
    }

    //Dotted name shown to the reader, as in Build.OutputType
    public string Key { get; }

    //Element the setting lives on
    public string Element { get; }

    //Attribute carrying the value
    public string Attribute { get; }

    //bool, enum or text
    public string Kind { get; }

    //Value the build uses when the attribute is absent
    public string Default { get; }

    //Whether removal is refused
    public bool Required { get; }

    //Accepted values of an enum setting
    public IReadOnlyList<string> Options { get; }
}

//One setting as the config file currently holds it
public sealed record NcConfigValue(NcConfigField Field, string Value, bool Present);

//NcConfig lists and edits the scalar settings of a config
//Tables such as packages, references and tasks have their own pages, so only single valued attributes are here
public static class NcConfig
{
    public const string BoolKind = "bool";

    public const string EnumKind = "enum";

    public const string TextKind = "text";

    //Element order of a written config, used when a setting creates its element
    private static readonly string[] Order = ["Check", "Build", "Server", "Client", "Sources", "Template"];

    //Every setting the loader reads
    public static readonly IReadOnlyList<NcConfigField> Fields =
    [
        new("Check.LangVersion", "Check", "LangVersion", EnumKind, NcCheck.DefaultLangVersion, false,
            "latest", "preview", "14.0", "13.0", "12.0"),

        new("Build.AssemblyName", "Build", "AssemblyName", TextKind, string.Empty, false),
        new("Build.Configuration", "Build", "Configuration", TextKind, NcBuild.DefaultConfiguration, true),
        new("Build.Output", "Build", "Output", TextKind, NcBuild.DefaultOutput, true),
        new("Build.OutputType", "Build", "OutputType", EnumKind, NcBuild.DefaultOutputType, true,
            NcBuild.OutputTypes),
        new("Build.RootNamespace", "Build", "RootNamespace", TextKind, string.Empty, false),
        new("Build.Nullable", "Build", "Nullable", BoolKind, "true", false),
        new("Build.ImplicitUsings", "Build", "ImplicitUsings", BoolKind, "true", false),
        new("Build.DependsOnModApi", "Build", "DependsOnModApi", BoolKind,
            NcBuild.DefaultDependsOnModApi ? "true" : "false", false),
        new("Build.AllowUnsafe", "Build", "AllowUnsafe", BoolKind,
            NcBuild.DefaultAllowUnsafe ? "true" : "false", false),
        new("Build.DefineConstants", "Build", "DefineConstants", TextKind, string.Empty, false),
        new("Build.ExtraArgs", "Build", "ExtraArgs", TextKind, string.Empty, false),

        new("Server.Cache", "Server", "Cache", TextKind, string.Empty, false),
        new("Server.Args", "Server", "Args", TextKind, string.Empty, false),
        new("Server.Debug", "Server", "Debug", BoolKind, "false", false),

        new("Client.Args", "Client", "Args", TextKind, string.Empty, false),
        new("Client.Version", "Client", "Version", TextKind, string.Empty, false),
        new("Client.Jar", "Client", "Jar", TextKind, string.Empty, false),

        new("Sources.Directory", "Sources", "Directory", TextKind, string.Empty, false),
        new("Sources.Url", "Sources", "Url", TextKind, string.Empty, false),
        new("Sources.Index", "Sources", "Index", TextKind, NcSource.DefaultIndex, false),
        new("Sources.Format", "Sources", "Format", EnumKind, NcSource.DefaultFormat, false, NcSource.Formats),
        new("Sources.Pattern", "Sources", "Pattern", TextKind, string.Empty, false),

        new("Template.Url", "Template", "Url", TextKind, string.Empty, false),
        new("Template.Base", "Template", "Base", TextKind, string.Empty, false),
    ];

    //Read the state of every setting, an absent one carrying its default so the page can offer it
    public static IReadOnlyList<NcConfigValue> Read(string path)
    {
        var values = new List<NcConfigValue>(Fields.Count);
        var document = TryLoad(path, out _);
        var root = document?.Root;

        foreach (var field in Fields)
        {
            var attribute = FindElement(root, field.Element)?.Attribute(field.Attribute);
            values.Add(new NcConfigValue(field, attribute?.Value ?? field.Default, attribute is not null));
        }

        return values;
    }

    //Set writes one value, creating the element when the config has none
    public static bool Set(string path, string key, string value, out string error)
    {
        error = string.Empty;
        var field = Find(key);
        if (field is null)
        {
            error = $"{key} is not a known setting";
            return false;
        }

        if (!TryNormalize(field, value, out var normalized, out error))
            return false;

        var document = TryLoad(path, out error);
        if (document?.Root is not { } root)
            return false;

        var element = FindElement(root, field.Element);
        if (element is null)
        {
            element = new XElement(field.Element);
            Insert(root, element, field.Element);
        }

        element.SetAttributeValue(field.Attribute, normalized);
        return Save(path, document, out error);
    }

    //Remove drops one value, and the element too when nothing else lives on it
    public static bool Remove(string path, string key, out string error)
    {
        error = string.Empty;
        var field = Find(key);
        if (field is null)
        {
            error = $"{key} is not a known setting";
            return false;
        }

        if (field.Required)
        {
            error = $"{key} is required and cannot be removed";
            return false;
        }

        var document = TryLoad(path, out error);
        if (document?.Root is not { } root)
            return false;

        var element = FindElement(root, field.Element);
        var attribute = element?.Attribute(field.Attribute);
        if (element is null || attribute is null)
            return true;

        attribute.Remove();

        //an element that carried only this attribute would otherwise stay behind as an empty node
        if (!element.HasAttributes && !element.HasElements && !element.Nodes().Any())
            element.Remove();

        return Save(path, document, out error);
    }

    //Find the field a dotted key names, ignoring case
    private static NcConfigField? Find(string key)
        => Fields.FirstOrDefault(field => string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase));

    //Find the element a setting lives on, matching the name the way the loader does
    private static XElement? FindElement(XElement? root, string name)
        => root?.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

    //Insert keeps the usual element order so a generated element does not land at the end
    private static void Insert(XElement root, XElement element, string name)
    {
        var position = Array.FindIndex(Order, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        foreach (var existing in root.Elements())
        {
            var other = Array.FindIndex(Order,
                item => string.Equals(item, existing.Name.LocalName, StringComparison.OrdinalIgnoreCase));
            if (position >= 0 && other > position)
            {
                existing.AddBeforeSelf(element);
                return;
            }
        }

        root.Add(element);
    }

    //TryNormalize turns what the editor wrote into the spelling the loader expects
    private static bool TryNormalize(NcConfigField field, string value, out string normalized, out string error)
    {
        var written = value.Trim();
        normalized = written;
        error = string.Empty;

        if (field.Kind == BoolKind)
        {
            switch (written.ToLowerInvariant())
            {
                case "true" or "1" or "yes" or "on":
                    normalized = "true";
                    return true;
                case "false" or "0" or "no" or "off" or "":
                    normalized = "false";
                    return true;
                default:
                    error = $"{field.Key} takes true or false";
                    return false;
            }
        }

        if (field.Kind == EnumKind)
        {
            var match = field.Options.FirstOrDefault(option =>
                string.Equals(option, written, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                error = $"{field.Key} takes one of {string.Join(", ", field.Options)}";
                return false;
            }

            normalized = match;
        }

        //text keeps what was typed, an empty value included
        return true;
    }

    private static XDocument? TryLoad(string path, out string error)
    {
        error = string.Empty;
        try
        {
            return XDocument.Load(path);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            error = $"cannot read {path}: {e.Message}";
            return null;
        }
    }

    private static bool Save(string path, XDocument document, out string error)
    {
        error = string.Empty;
        try
        {
            XmlFile.Save(path, document);
            return true;
        }
        catch (IOException e)
        {
            error = $"cannot write {path}: {e.Message}";
            return false;
        }
    }
}
