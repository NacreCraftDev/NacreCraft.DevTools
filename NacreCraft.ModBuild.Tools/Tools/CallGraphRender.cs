using System.Text;
using Microsoft.CodeAnalysis;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Tools;

//CallGraphRender turns a CallGraph into text: the indented list or the plain report
//Colours are ansi escapes so one line can mix several tones; a file output and a redirected console keep plain text
internal static class CallGraphRender
{
    //Hard limits keep a very wide or deep graph inside a bounded canvas and stack
    private const int MaxDepth = 256;
    private const int MaxNodes = 20000;

    private const string Reset = "\u001b[0m";

    //Render builds the whole report and returns it with or without ansi colours
    public static string Render(CallGraph graph, IReadOnlyList<CallGraph.Node> entries, string rootDirectory, int depth,
        bool external, bool cycles, bool color)
    {
        var report = new Report();
        var nodes = graph.Nodes();
        var calls = nodes.Sum(node => node.Outgoing.Count);
        var outside = nodes.Sum(node => node.Outgoing.Count(edge => !CallGraph.Local(edge.Target)));

        report.Add(new Segment($"call graph for {rootDirectory}"));
        report.Add(new Segment($"  {nodes.Count} method(s), {calls} call(s), {outside} external call(s)", Tone.Dim));
        report.Blank();
        report.Add(new Segment($"entries ({entries.Count}):"));
        foreach (var entry in entries)
        {
            var parts = new List<Segment> { new("  ", Tone.Dim) };
            AddLabel(parts, entry.Method, rootDirectory, Tone.Entry);
            report.Add(parts);
        }

        report.Blank();

        //a method body is expanded once for the whole report, so every later caller only points back at it
        var expanded = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var entry in entries)
        {
            Flat(graph, entry, rootDirectory, depth, external, expanded, report);
            report.Blank();
        }

        if (!cycles)
            return report.Render(color);

        var found = graph.Cycles();
        report.Add(new Segment($"call cycles ({found.Count}):", found.Count == 0 ? Tone.Plain : Tone.Warning));
        foreach (var cycle in found)
        {
            var position = cycle.Site.GetLineSpan();
            var path = string.IsNullOrEmpty(position.Path)
                ? string.Empty
                : $" ({Relative(rootDirectory, position.Path)}:{position.StartLinePosition.Line + 1})";
            var reason = cycle.Conditional
                ? "no exit on the cycle and its condition never flips"
                : "no exit on the cycle and every call runs outright";
            report.Add(new Segment($"  {cycle.Chain}{path} {reason}", Tone.Warning));
        }

        return report.Render(color);
    }

    //Label names a method with the file it is declared in, which tells apart methods with the same name
    public static string Label(IMethodSymbol method, string rootDirectory)
    {
        var name = CallGraph.Name(method);
        var location = method.Locations.FirstOrDefault(item => item.IsInSource);
        if (location is null)
            return name;

        var position = location.GetLineSpan();
        return $"{name} ({Relative(rootDirectory, position.Path)}:{position.StartLinePosition.Line + 1})";
    }

    //Flat draws the indented list, the layout used before the organisation tree existed
    private static void Flat(CallGraph graph, CallGraph.Node entry, string rootDirectory, int limit, bool external,
        HashSet<IMethodSymbol> expanded, Report report)
    {
        AddRoot(report, entry.Method, rootDirectory);

        //the entry body is shown here, so a later caller only points back at it
        expanded.Add(entry.Method);
        var stack = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { entry.Method };
        var budget = MaxNodes;
        FlatBelow(graph, entry, string.Empty, rootDirectory, Guard(limit), external, stack, expanded, ref budget, 0,
            report);

        if (budget <= 0)
            report.Add(new Segment($"  ... node limit {MaxNodes} reached", Tone.Dim));
    }

    private static void FlatBelow(CallGraph graph, CallGraph.Node node, string prefix, string rootDirectory, int limit,
        bool external, HashSet<IMethodSymbol> stack, HashSet<IMethodSymbol> expanded, ref int budget, int depth,
        Report report)
    {
        var branches = Groups(node, external);
        for (var index = 0; index < branches.Count; index++)
        {
            if (budget-- <= 0)
                return;

            var branch = branches[index];
            var last = index == branches.Count - 1;
            var (parts, expand) = Describe(branch, rootDirectory, limit, stack, expanded, depth);

            var line = new List<Segment> { new(prefix + (last ? "└─ " : "├─ "), Tone.Dim) };
            line.AddRange(parts);
            report.Add(line);

            if (!expand)
                continue;

            var child = graph.NodeOf(branch.Target);
            if (child is null)
                continue;

            if (child.Outgoing.Count > 0)
                expanded.Add(branch.Target);

            stack.Add(branch.Target);
            FlatBelow(graph, child, prefix + (last ? "    " : "│   "), rootDirectory, limit, external, stack, expanded,
                ref budget, depth + 1, report);
            stack.Remove(branch.Target);
        }
    }

    //Guard keeps a caller supplied depth inside the hard limit
    private static int Guard(int limit)
        => limit > 0 ? Math.Min(limit, MaxDepth) : MaxDepth;

    //Describe writes one branch as label segments and answers whether the target is expanded below it
    private static (List<Segment> Parts, bool Expand) Describe(Branch branch, string rootDirectory, int limit,
        HashSet<IMethodSymbol> stack, HashSet<IMethodSymbol> expanded, int depth)
    {
        var parts = new List<Segment>();
        if (!string.IsNullOrEmpty(branch.Condition))
            parts.Add(new Segment($"[{Trim(branch.Condition)}] ", Tone.Condition));

        if (!CallGraph.Local(branch.Target))
        {
            AddLabel(parts, branch.Target, rootDirectory, Tone.External, branch.Count);
            parts.Add(new Segment(" [external]", Tone.External));
            return (parts, false);
        }

        if (stack.Contains(branch.Target))
        {
            AddLabel(parts, branch.Target, rootDirectory, Tone.Cycle, branch.Count);
            parts.Add(new Segment(" [recursive]", Tone.Cycle));
            return (parts, false);
        }

        //a body already shown earlier in the report is named here instead of being walked a second time
        if (expanded.Contains(branch.Target))
        {
            AddLabel(parts, branch.Target, rootDirectory, Tone.Dim, branch.Count);
            parts.Add(new Segment(" [expanded]", Tone.Dim));
            return (parts, false);
        }

        if (limit > 0 && depth + 1 >= limit)
        {
            AddLabel(parts, branch.Target, rootDirectory, Tone.Depth, branch.Count);
            parts.Add(new Segment(" [depth]", Tone.Depth));
            return (parts, false);
        }

        AddLabel(parts, branch.Target, rootDirectory, Tone.Plain, branch.Count);
        return (parts, true);
    }

    private static void AddRoot(Report report, IMethodSymbol method, string rootDirectory)
    {
        var parts = new List<Segment>();
        AddLabel(parts, method, rootDirectory, Tone.Entry);
        parts.Add(new Segment(" [entry]", Tone.Entry));
        report.Add(parts);
    }

    private static void AddLabel(List<Segment> parts, IMethodSymbol method, string rootDirectory, Tone tone,
        int count = 1)
    {
        parts.Add(new Segment(CallGraph.Name(method), tone));
        if (count > 1)
            parts.Add(new Segment($" x{count}", Tone.Dim));

        var location = method.Locations.FirstOrDefault(item => item.IsInSource);
        if (location is null)
            return;

        var position = location.GetLineSpan();
        parts.Add(new Segment($" ({Relative(rootDirectory, position.Path)}:{position.StartLinePosition.Line + 1})",
            Tone.Dim));
    }

    private static List<CallGraph.Edge> Sorted(CallGraph.Node node, bool external)
        => node.Outgoing
            .Where(edge => external || CallGraph.Local(edge.Target))
            .OrderBy(edge => edge.Site.SourceSpan.Start)
            .ThenBy(edge => CallGraph.Name(edge.Target), StringComparer.Ordinal)
            .ToList();

    //Groups folds several calls from one method to the same target into one branch that carries the call count
    private static List<Branch> Groups(CallGraph.Node node, bool external)
    {
        var groups = new List<Branch>();
        var positions = new Dictionary<IMethodSymbol, int>(SymbolEqualityComparer.Default);
        foreach (var edge in Sorted(node, external))
        {
            if (positions.TryGetValue(edge.Target, out var at))
            {
                groups[at] = groups[at].Add(edge.Condition);
                continue;
            }

            positions[edge.Target] = groups.Count;
            groups.Add(new Branch(edge.Target, edge.Condition));
        }

        return groups;
    }

    //Branch one target a method calls, with the number of calls and the conditions those calls sit under
    private sealed record Branch(IMethodSymbol Target, string? Condition, int Count = 1)
    {
        public Branch Add(string? condition) => this with
        {
            Count = Count + 1,
            Condition = Merge(Condition, condition),
        };
    }

    private static string? Merge(string? first, string? second)
    {
        if (string.IsNullOrEmpty(second))
            return first;
        if (string.IsNullOrEmpty(first))
            return second;
        return first.Contains(second, StringComparison.Ordinal) ? first : first + ", " + second;
    }

    private static string Trim(string text)
        => text.Length <= 48 ? text : text[..45] + "...";

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Code(Tone tone) => tone switch
    {
        Tone.Dim or Tone.External or Tone.Depth => "\u001b[90m",
        Tone.Entry => "\u001b[36m",
        Tone.Condition => "\u001b[33m",
        Tone.Cycle or Tone.Warning => "\u001b[31m",
        _ => string.Empty,
    };

    private enum Tone
    {
        Plain,
        Dim,
        Entry,
        Condition,
        External,
        Cycle,
        Depth,
        Warning,
    }

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
