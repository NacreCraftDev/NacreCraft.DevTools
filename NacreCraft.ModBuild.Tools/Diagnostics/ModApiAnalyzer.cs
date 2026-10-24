using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Diagnostics;

//ModApiAnalyzer grades the project's api usages against the catalog and turns problems into diagnostics
//a type absent from the catalog is an error that blocks the build, while a mismatched member is only a warning since the catalog may be incomplete
public static class ModApiAnalyzer
{
    //UnknownTypeCode is reported when the type is not in the catalog
    public const string UnknownTypeCode = "NC0001";

    //UnknownMemberCode is reported when the type is in the catalog but the member is not
    public const string UnknownMemberCode = "NC0002";

    //Analyze scans the sources and collects catalog mismatches into bag
    //it returns the identifiers already reported so the Roslyn pass can dedupe and avoid reporting one spot twice
    public static HashSet<string> Analyze(string root, TemplateCatalog catalog, DiagnosticBag bag)
    {
        var covered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in ApiUsage.Scan(root, catalog).Items)
        {
            if (item.State == UsageState.Ok)
                continue;

            var location = item.Locations.Count > 0 ? item.Locations[0] : null;
            var column = location is null ? 1 : location.Text.IndexOf(item.Symbol, StringComparison.Ordinal) + 1;
            var unknown = item.State == UsageState.Missing;

            covered.Add(item.Type);
            covered.Add(item.Member);

            var fixes = new List<Suggestion>();
            if (location is not null)
            {
                //the nearest candidate becomes an actionable replacement
                //an unknown type replaces the type segment, an unknown member replaces the member segment
                var first = item.Candidates.FirstOrDefault();
                if (first is not null)
                {
                    var replacement = unknown ? first : LastSegment(first);
                    var start = Math.Max(1, column) + (unknown ? 0 : item.Type.Length + 1);
                    var length = unknown ? item.Type.Length : item.Member.Length;

                    fixes.Add(Suggestion.Replace(
                        $"did you mean `{replacement}`?",
                        location.Line,
                        start,
                        length,
                        replacement,
                        Applicability.MaybeIncorrect));
                }

                //remaining candidates merge into one line that explains itself, as there is no heading
                var rest = item.Candidates.Skip(1).ToList();
                if (rest.Count > 0)
                    fixes.Add(Suggestion.Text($"other candidates: {string.Join(", ", rest)}"));
            }

            bag.Add(new Diagnostic(
                unknown ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                unknown ? UnknownTypeCode : UnknownMemberCode,
                unknown
                    ? $"the catalog does not declare type `{item.Type}`"
                    : $"type `{item.Type}` has no member `{item.Member}`",
                location?.File ?? "?",
                location?.Line ?? 0,
                Math.Max(1, column),
                item.Symbol.Length,
                location?.Text ?? string.Empty,
                unknown
                    ? "the name may be misspelled; the catalog is the authoritative api list"
                    : $"`{item.Member}` is not a member of {item.Type}",
                string.Empty,
                fixes));
        }

        return covered;
    }

    //LastSegment takes the member name from a Type.Member candidate
    private static string LastSegment(string symbol)
    {
        var dot = symbol.LastIndexOf('.');
        return dot >= 0 ? symbol[(dot + 1)..] : symbol;
    }
}
