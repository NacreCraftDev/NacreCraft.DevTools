using System.Text.RegularExpressions;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Diagnostics;

//UsageState how plausible one api usage in the project looks
public enum UsageState
{
    //both the type and the member are in the catalog
    Ok,
    //the type is in the catalog but the member is missing from its member table
    Warn,
    //the name looks like a mod api yet is absent from the catalog
    Missing,
}

//UsageLocation where one usage lands in the source
public sealed record UsageLocation(string File, int Line, string Text);

//ApiUsageItem everything about one usage; the panel lists these and diagnostics derive fixes from them
public sealed class ApiUsageItem
{
    //in Type.Member form
    public required string Symbol { get; init; }

    public required UsageState State { get; init; }

    public required string Type { get; init; }

    public required string Member { get; init; }

    //likely intended apis, nearest first; Warn lists members of the same type and Missing lists catalog types
    public required List<string> Candidates { get; init; }

    //where it appears in the source
    public required List<UsageLocation> Locations { get; init; }
}

//ApiUsage the usage table produced by scanning the project sources
//the catalog is the authoritative api list, so every usage is graded against it
public sealed class ApiUsage
{
    //MaxCandidates caps how many candidates a fix hint lists
    private const int MaxCandidates = 6;

    //MaxLocations caps how many occurrences are recorded per symbol
    private const int MaxLocations = 3;

    //TypeDeclaration matches type definitions in the source so the project's own types are exempt from catalog checks
    //the catalog governs only the api the kernel exposes to mods, and a typo in a local type surfaces at compile time
    private static readonly Regex TypeDeclaration = new(
        @"\b(?:class|struct|interface|enum|record)\s+(?:class\s+|struct\s+)?([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private readonly Dictionary<string, ApiUsageItem> _items = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ApiUsageItem> Items => _items.Values;

    //Scan walks the sources under root and grades every usage against the catalog
    public static ApiUsage Scan(string root, TemplateCatalog? catalog)
    {
        var usage = new ApiUsage();
        if (catalog is null)
        {
            Trace.Log("catalog is not loaded, skipping the source scan");
            return usage;
        }

        var pattern = BuildPattern(catalog.Grade);
        if (pattern is null)
        {
            Trace.Log("grade rule is empty, skipping the source scan");
            return usage;
        }

        //entry name to member table; a null table means this entry does not check members
        var members = new Dictionary<string, HashSet<string>?>(StringComparer.Ordinal);
        foreach (var entry in catalog.Apis)
            members[Name(entry.Id)] = entry.Members.Count > 0
                ? new HashSet<string>(entry.Members, StringComparer.Ordinal)
                : null;

        var knownTypes = members.Keys.ToList();
        var files = SourceFiles.Enumerate(root).ToList();
        Trace.Log($"scanned {files.Count} source file(s) under {root}");

        //Read every source up front so all project-defined types are collected in one pass
        //scanning file by file would treat a type defined in a later file as absent from the catalog
        //matching runs against the masked text while positions still come from the original, whose indices line up one to one
        var sources = new (string File, string Text, string Masked)?[files.Count];
        var declarations = new HashSet<string>?[files.Count];

        //the files are independent, so reading, masking and the declaration scan all run side by side
        Parallel.For(0, files.Count, index =>
        {
            var file = files[index];
            var text = SourceTextCache.Read(file);
            if (text is null)
            {
                Trace.Log($"cannot read {file}");
                return;
            }

            var masked = Mask(text);
            sources[index] = (file, text, masked);

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match declaration in TypeDeclaration.Matches(masked))
                names.Add(declaration.Groups[1].Value);
            declarations[index] = names;
        });

        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var names in declarations)
        {
            if (names is not null)
                defined.UnionWith(names);
        }

        Trace.Log($"found {defined.Count} type declaration(s) in the project itself");

        foreach (var source in sources)
        {
            if (source is not { } entry)
                continue;

            var (file, text, masked) = entry;
            foreach (Match match in pattern.Matches(masked))
            {
                var type = match.Groups[1].Value;
                var member = match.Groups[2].Value;
                var symbol = $"{type}.{member}";

                if (!usage._items.TryGetValue(symbol, out var item))
                {
                    //a type the project declares passes outright, even when the catalog holds a same-named entry
                    var state = defined.Contains(type)
                        ? UsageState.Ok
                        : Judge(members, type, member);
                    item = new ApiUsageItem
                    {
                        Symbol = symbol,
                        State = state,
                        Type = type,
                        Member = member,
                        Candidates = Candidates(members, knownTypes, type, member, state),
                        Locations = new List<UsageLocation>(),
                    };
                    usage._items[symbol] = item;
                    Trace.Log($"{symbol} -> {state}");
                }

                if (item.Locations.Count < MaxLocations)
                    item.Locations.Add(new UsageLocation(
                        Path.GetRelativePath(root, file).Replace('\\', '/'),
                        LineOf(text, match.Index),
                        LineText(text, match.Index)));
            }
        }

        return usage;
    }

    //BuildPattern assembles the Type.Member regex from the catalog's grade rules
    //with no rules it returns null so trivial Type.Member references are not all read as mod api calls
    private static Regex? BuildPattern(GradeRule rule)
    {
        var alternatives = new List<string>();
        foreach (var type in rule.Types)
        {
            if (!string.IsNullOrWhiteSpace(type))
                alternatives.Add(Regex.Escape(type));
        }
        foreach (var prefix in rule.Prefixes)
        {
            if (!string.IsNullOrWhiteSpace(prefix))
                alternatives.Add($"{Regex.Escape(prefix)}[A-Za-z0-9_]*");
        }

        if (alternatives.Count == 0)
            return null;

        return new Regex(
            $@"\b({string.Join("|", alternatives)})\.([A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Compiled);
    }

    //Candidates builds the fix hints; Warn searches members of the same type and Missing searches catalog type names, both ordered by edit distance
    private static List<string> Candidates(
        Dictionary<string, HashSet<string>?> members,
        List<string> knownTypes,
        string type,
        string member,
        UsageState state)
    {
        if (state == UsageState.Warn)
        {
            if (!members.TryGetValue(type, out var table) || table is null)
                return new List<string>();

            //a type can carry dozens of members, so distant ones would only add noise
            var limit = Math.Max(2, member.Length / 2);
            return table
                .Select(name => (Name: name, Distance: Similarity.Distance(member, name)))
                .Where(item => item.Distance <= limit)
                .OrderBy(item => item.Distance)
                .Take(MaxCandidates)
                .Select(item => $"{type}.{item.Name}")
                .ToList();
        }

        if (state == UsageState.Missing)
        {
            //there are few types in total, so list them all with the closest first
            return knownTypes
                .OrderBy(name => Similarity.Distance(type, name))
                .Take(MaxCandidates)
                .ToList();
        }

        return new List<string>();
    }

    //LineOf returns the 1-based line that contains a character index
    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }
        return line;
    }

    //LineText returns the line text at an index with indentation kept so the position arrow lines up
    private static string LineText(string text, int index)
    {
        var start = index;
        while (start > 0 && text[start - 1] != '\n')
            start--;

        var end = text.IndexOf('\n', index);
        if (end < 0)
            end = text.Length;

        return text[start..end].TrimEnd('\r');
    }

    //Mask blanks out comments and string literals while keeping newlines, so masked and original text share the same indices
    private static string Mask(string text)
    {
        var buffer = text.ToCharArray();
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (current == '/' && Looking(text, index + 1, '/'))
            {
                while (index < text.Length && text[index] != '\n')
                    buffer[index++] = ' ';
                continue;
            }

            //block comment: blank through the closing pair, or to the end when it is unterminated
            if (current == '/' && Looking(text, index + 1, '*'))
            {
                buffer[index++] = ' ';
                buffer[index++] = ' ';
                while (index < text.Length)
                {
                    var ends = text[index] == '*' && Looking(text, index + 1, '/');
                    if (text[index] != '\n')
                        buffer[index] = ' ';
                    index++;
                    if (!ends)
                        continue;

                    if (index < text.Length)
                        buffer[index++] = ' ';
                    break;
                }
                continue;
            }

            if (current is '"' or '\'' or '@' or '$' && MaskLiteral(text, buffer, ref index))
                continue;

            index++;
        }

        return new string(buffer);
    }

    //MaskLiteral blanks one literal and reports whether it recognized one
    //@ and $ are only prefixes, so they pass through untouched without a following quote since an @ inside an identifier is no string opener
    private static bool MaskLiteral(string text, char[] buffer, ref int index)
    {
        var start = index;
        while (index < text.Length && text[index] is '@' or '$')
            index++;

        if (index >= text.Length || text[index] is not ('"' or '\''))
        {
            index = start;
            return false;
        }

        var quote = text[index];
        var verbatim = index > start;
        var raw = quote == '"' && Looking(text, index + 1, '"') && Looking(text, index + 2, '"');

        //opening quotes; a raw string opens with three
        for (var step = 0; step < (raw ? 3 : 1) && index < text.Length; step++)
            buffer[index++] = ' ';

        while (index < text.Length)
        {
            //a raw string closes on three quotes, and single or double quotes inside do not count
            if (raw && Looking(text, index, quote) && Looking(text, index + 1, quote) && Looking(text, index + 2, quote))
            {
                for (var step = 0; step < 3; step++)
                    buffer[index++] = ' ';
                break;
            }

            //escape: blank the backslash together with the next character, but keep newlines so lines do not shift
            if (!raw && !verbatim && text[index] == '\\')
            {
                buffer[index++] = ' ';
                if (index < text.Length && text[index] != '\n')
                    buffer[index++] = ' ';
                continue;
            }

            //in a verbatim string a doubled quote is an escape, not the terminator
            if (!raw && verbatim && Looking(text, index, quote) && Looking(text, index + 1, quote))
            {
                buffer[index++] = ' ';
                buffer[index++] = ' ';
                continue;
            }

            var closing = text[index] == quote;
            if (text[index] != '\n')
                buffer[index] = ' ';
            index++;

            if (closing)
                break;
        }

        return true;
    }

    //Looking reports whether the character at an index matches, counting out-of-range as no match
    private static bool Looking(string text, int index, char value)
        => index >= 0 && index < text.Length && text[index] == value;

    //Name takes the last segment of an entry id, which is the type name written in code
    private static string Name(string id) => id.Split('.')[^1];

    //Judge decides which grade one usage gets
    private static UsageState Judge(Dictionary<string, HashSet<string>?> members, string type, string member)
    {
        if (!members.TryGetValue(type, out var table))
            return UsageState.Missing;
        if (table is null)
            return UsageState.Ok;
        return table.Contains(member) ? UsageState.Ok : UsageState.Warn;
    }
}
