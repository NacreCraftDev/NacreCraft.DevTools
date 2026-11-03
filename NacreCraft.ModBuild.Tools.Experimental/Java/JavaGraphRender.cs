using System.Text;

namespace NacreCraft.ModBuild.Tools.Experimental.Java;

//JavaGraphRender prints a JavaCallGraph as an indented call tree
//It follows the layout of the shipped callgraph report, minus the cycle section, because this graph never checks for cycles
internal static class JavaGraphRender
{
    //hard limits keep a very wide or deep graph inside a bounded canvas and stack
    private const int MaxDepth = 256;
    private const int MaxNodes = 20000;

    private const string Reset = "\u001b[0m";

    public static string Render(JavaCallGraph graph, IReadOnlyList<JavaCallGraph.Method> entries, string rootDirectory,
        int depth, bool external, bool color)
    {
        var report = new Report();
        var methods = graph.Local.ToList();

        report.Add(new Segment($"java call graph for {rootDirectory}"));
        report.Add(new Segment(
            $"  {methods.Count} method(s), {graph.CallCount} call(s), {graph.ExternalCount} external call(s), {graph.Unresolved} unresolved",
            Tone.Dim));
        report.Blank();

        report.Add(new Segment($"entries ({entries.Count}):"));
        foreach (var entry in entries)
            report.Add(new List<Segment> { new("  ", Tone.Dim), Label(entry, Tone.Entry) });

        report.Blank();

        //a body is expanded once for the whole report, so every later caller only points back at it
        var expanded = new HashSet<JavaCallGraph.Method>();
        foreach (var entry in entries)
        {
            AddRoot(report, entry);
            expanded.Add(entry);

            var budget = MaxNodes;
            Below(graph, entry, string.Empty, depth, external, expanded, ref budget, 0, report);
            if (budget <= 0)
                report.Add(new Segment($"  ... node limit {MaxNodes} reached", Tone.Dim));

            report.Blank();
        }

        return report.Render(color);
    }

    private static void Below(JavaCallGraph graph, JavaCallGraph.Method node, string prefix, int limit, bool external,
        HashSet<JavaCallGraph.Method> expanded, ref int budget, int depth, Report report)
    {
        var branches = Groups(node, external);
        for (var index = 0; index < branches.Count; index++)
        {
            if (budget-- <= 0)
                return;

            var branch = branches[index];
            var last = index == branches.Count - 1;
            var parts = new List<Segment>();
            var expand = Describe(branch, limit, expanded, depth, parts);

            var line = new List<Segment> { new(prefix + (last ? "└─ " : "├─ "), Tone.Dim) };
            line.AddRange(parts);
            report.Add(line);

            if (!expand)
                continue;

            if (branch.Target.Outgoing.Count > 0)
                expanded.Add(branch.Target);

            Below(graph, branch.Target, prefix + (last ? "    " : "│   "), limit, external, expanded, ref budget,
                depth + 1, report);
        }
    }

    private static bool Describe(Branch branch, int limit, HashSet<JavaCallGraph.Method> expanded, int depth,
        List<Segment> parts)
    {
        if (!branch.Target.Local)
        {
            parts.Add(Label(branch.Target, Tone.External, branch.Count));
            parts.Add(new Segment(" [external]", Tone.External));
            return false;
        }

        if (expanded.Contains(branch.Target))
        {
            parts.Add(Label(branch.Target, Tone.Dim, branch.Count));
            parts.Add(new Segment(" [expanded]", Tone.Dim));
            return false;
        }

        if (limit > 0 && depth + 1 >= limit)
        {
            parts.Add(Label(branch.Target, Tone.Depth, branch.Count));
            parts.Add(new Segment(" [depth]", Tone.Depth));
            return false;
        }

        parts.Add(Label(branch.Target, Tone.Plain, branch.Count));
        return true;
    }

    private static void AddRoot(Report report, JavaCallGraph.Method method)
    {
        var parts = new List<Segment> { Label(method, Tone.Entry) };
        parts.Add(new Segment(" [entry]", Tone.Entry));
        report.Add(parts);
    }

    private static Segment Label(JavaCallGraph.Method method, Tone tone, int count = 1)
    {
        var text = method.Signature;
        if (count > 1)
            text += $" x{count}";
        if (method.File.Length > 0)
            text += $" ({method.File}:{method.Line})";

        return new Segment(text, tone);
    }

    private static List<Branch> Groups(JavaCallGraph.Method node, bool external)
    {
        var groups = new List<Branch>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var edge in node.Outgoing
                     .Where(edge => external || edge.Target.Local)
                     .OrderBy(edge => edge.Line)
                     .ThenBy(edge => edge.Target.Signature, StringComparer.Ordinal))
        {
            if (positions.TryGetValue(edge.Target.Key, out var at))
            {
                groups[at] = groups[at] with { Count = groups[at].Count + 1 };
                continue;
            }

            positions[edge.Target.Key] = groups.Count;
            groups.Add(new Branch(edge.Target));
        }

        return groups;
    }

    private static string Code(Tone tone) => tone switch
    {
        Tone.Dim or Tone.External or Tone.Depth => "\u001b[90m",
        Tone.Entry => "\u001b[36m",
        Tone.Warning => "\u001b[33m",
        _ => string.Empty,
    };

    private enum Tone
    {
        Plain,
        Dim,
        Entry,
        External,
        Depth,
        Warning,
    }

    private readonly record struct Branch(JavaCallGraph.Method Target, int Count = 1);

    private readonly record struct Segment(string Text, Tone Tone = Tone.Plain);

    private sealed class Report
    {
        private readonly List<List<Segment>> _lines = new();

        public void Add(IEnumerable<Segment> segments)
            => _lines.Add(segments.Where(segment => segment.Text.Length > 0).ToList());

        public void Add(params Segment[] segments) => Add((IEnumerable<Segment>)segments);

        public void Blank() => _lines.Add(new List<Segment>());

        public string Render(bool color)
        {
            var builder = new StringBuilder();
            foreach (var line in _lines)
            {
                foreach (var segment in line)
                {
                    if (!color || segment.Tone == Tone.Plain)
                    {
                        builder.Append(segment.Text);
                        continue;
                    }

                    builder.Append(Code(segment.Tone)).Append(segment.Text).Append(Reset);
                }

                builder.Append(Environment.NewLine);
            }

            return builder.ToString();
        }
    }
}
