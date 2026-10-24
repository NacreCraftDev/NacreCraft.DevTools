using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NacreCraft.ModBuild.Core;

//TemplateEntry, one api entry in the catalog
public sealed class TemplateEntry
{
    //Full api identifier such as NacreCraft.ModApi.Wrapper.NcPlayer
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    //Example file path relative to base
    public string Template { get; set; } = string.Empty;

    //Member names the api exposes, used to judge usage in the project; empty skips member checks and any matching type name counts as correct
    public List<string> Members { get; set; } = new();
}

//Recognition rules for scanning project sources; a symbol written as Type.Member counts as a mod api only when the type matches these rules
public sealed class GradeRule
{
    //Type name prefixes such as Nc
    public List<string> Prefixes { get; set; } = new() { "Nc" };

    //Type names recognized without a prefix, such as the three event facades
    public List<string> Types { get; set; } = new() { "ServerEvents", "ClientEvents", "NetworkEvents" };
}

//Model of NacreCraftTemplate.yaml; fields map one-to-one with the yaml and extra keys are ignored so the catalog can grow later
public sealed class TemplateCatalog
{
    //Base location the examples are fetched from
    public string Base { get; set; } = string.Empty;

    //Document rendered by the graphical panel, relative to base
    public string Document { get; set; } = string.Empty;

    //Shape rules for recognizing api usage in the project
    public GradeRule Grade { get; set; } = new();

    public List<TemplateEntry> Apis { get; set; } = new();

    //Read the local catalog, returning null when it cannot be read or parsed
    public static TemplateCatalog? Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException e)
        {
            Console.WriteLine($"Failed to read {path}: {e.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine($"Template catalog {path} is empty");
            return null;
        }

        try
        {
            return new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<TemplateCatalog>(text);
        }
        catch (YamlException e)
        {
            Console.WriteLine($"Template catalog {path} is not valid yaml: {e.Message}");
            return null;
        }
    }

    //Join a relative path onto the base URL, trimming the slash on each side
    public string ResolveUrl(string relative)
        => $"{Base.TrimEnd('/')}/{relative.TrimStart('/')}";

    //Whether an entry id matches the filter pattern; ? and * both match any run of characters while the rest is literal, matching is case-insensitive and an empty pattern matches everything
    public static bool Matches(string id, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        //Escape first and only then open the wildcards so regex metacharacters in user input cannot take effect
        var body = Regex.Escape(pattern).Replace(@"\?", ".*").Replace(@"\*", ".*");
        return Regex.IsMatch(id, $"^.*{body}.*$", RegexOptions.IgnoreCase);
    }
}
