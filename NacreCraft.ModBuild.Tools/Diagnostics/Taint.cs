using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;

namespace NacreCraft.ModBuild.Diagnostics;

//TaintKind what a rule matches: where untrusted data enters, where it must not reach, or what cleans it
internal enum TaintKind
{
    Source,
    Sink,
    Sanitizer,
}

//TaintRule one pattern for a method, matched against its name, type.name and full signature
internal sealed record TaintRule(TaintKind Kind, string Pattern);

//TaintPath one route from a root method to a sink, with the call chain that carries the value
internal sealed record TaintPath(string Root, string Chain, Location Sink, string Text);

//TaintOptions what the analysis looks at and how far it follows a value
internal sealed record TaintOptions(
    IReadOnlyList<string> Roots,
    IReadOnlyCollection<string> Classes,
    IReadOnlyCollection<string> Methods,
    IReadOnlyList<TaintRule> Rules,
    int Depth,
    bool Interprocedural,
    bool Fields,
    bool External,
    int MaxPaths,
    bool MemorySinks,
    IReadOnlyList<string> RootParameters)
{
    //Default the shape the cli uses: only local calls, fields, a bounded walk and the memory catalog as sinks
    public static TaintOptions Default { get; } =
        new([], [], [], [], 8, true, true, true, 200, false, []);
}

//TaintMatcher turns rule patterns into one lookup over the methods the analysis meets
internal sealed class TaintMatcher
{
    private readonly List<(TaintKind Kind, Regex Pattern)> _rules = new();

    public TaintMatcher(IEnumerable<TaintRule> rules)
    {
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
                continue;

            _rules.Add((rule.Kind, new Regex(Pattern(rule.Pattern), RegexOptions.CultureInvariant)));
        }
    }

    public TaintKind? Match(IMethodSymbol method)
    {
        var type = method.ContainingType;
        var signatures = new[]
        {
            method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            type is null ? method.Name : $"{type.ToDisplayString()}.{method.Name}",
            type is null ? method.Name : $"{type.Name}.{method.Name}",
            method.Name,
        };

        foreach (var (kind, pattern) in _rules)
        {
            foreach (var signature in signatures)
            {
                if (pattern.IsMatch(signature))
                    return kind;
            }
        }

        return null;
    }

    //Load reads a json file holding sources, sinks and sanitizers, each an array of patterns
    public static IReadOnlyList<TaintRule> Load(string path)
    {
        var root = JObject.Parse(File.ReadAllText(path));
        var rules = new List<TaintRule>();
        Add(root["sources"], TaintKind.Source);
        Add(root["sinks"], TaintKind.Sink);
        Add(root["sanitizers"], TaintKind.Sanitizer);
        return rules;

        void Add(JToken? token, TaintKind kind)
        {
            if (token is not JArray array)
                return;

            foreach (var item in array)
            {
                if (item.Value<string>() is { Length: > 0 } value)
                    rules.Add(new TaintRule(kind, value));
            }
        }
    }

    //Pattern turns * and ? into a regex, leaving everything else literal
    private static string Pattern(string pattern)
    {
        var text = Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);
        return $"^{text}$";
    }
}
