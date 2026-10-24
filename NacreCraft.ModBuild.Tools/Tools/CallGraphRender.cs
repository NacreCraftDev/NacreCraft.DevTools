using System.Text;
using Microsoft.CodeAnalysis;
using NacreCraft.ModBuild.Diagnostics;

namespace NacreCraft.ModBuild.Tools;

//CallGraphRender turns a CallGraph into text: the indented list, the top down organisation tree, or the plain report
//Colours are ansi escapes so one line can mix several tones; a file output and a redirected console keep plain text
internal static class CallGraphRender
{
    //Space between two sibling subtrees in the organisation tree
    private const int Gap = 3;

    //Hard limits keep a very wide or deep graph inside a bounded canvas and stack
    private const int MaxWidth = 200;
    private const int MaxDepth = 256;
    private const int MaxNodes = 20000;

    private const string Reset = "\u001b[0m";

    //Render builds the whole report and returns it with or without ansi colours
    public static string Render(CallGraph graph, IReadOnlyList<CallGraph.Node> entries, string rootDirectory, int depth,
        bool external, bool cycles, bool tree, bool color)
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
            if (tree)
                Tree(graph, entry, rootDirectory, depth, external, expanded, report);
            else
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

    //Label names a method with the file it is declared in, which a tree of same named methods needs
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

    //Tree draws one entry as a top down organisation tree, root on top and every branch on its own row
    private static void Tree(CallGraph graph, CallGraph.Node entry, string rootDirectory, int limit, bool external,
        HashSet<IMethodSymbol> expanded, Report report)
    {
        var label = new List<Segment>();
        AddLabel(label, entry.Method, rootDirectory, Tone.Entry);
        label.Add(new Segment(" [entry]", Tone.Entry));

        expanded.Add(entry.Method);
        var stack = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { entry.Method };
        var budget = MaxNodes;
        var root = Build(graph, entry.Method, label, rootDirectory, Guard(limit), external, stack, expanded,
            ref budget, 0);
        Measure(root);

        //a wide graph is shown from its middle, since the full canvas would be mostly empty on both ends
        var width = Math.Min(root.Width, MaxWidth);
        var offset = root.Width > MaxWidth ? (root.Width - MaxWidth) / 2 : 0;

        var canvas = new List<Cell[]>();
        Place(root, -offset, 0, width, canvas);
        foreach (var row in canvas)
        {
            var cells = Cells(row).ToList();
            if (cells.Count > 0)
                report.Add(cells);
        }

        if (budget <= 0)
            report.Add(new Segment($"  ... node limit {MaxNodes} reached", Tone.Dim));
    }

    //Build turns one method and its calls into a box tree, ending a branch on an external call, a cycle or the limit
    private static Box Build(CallGraph graph, IMethodSymbol method, List<Segment> label, string rootDirectory, int limit,
        bool external, HashSet<IMethodSymbol> stack, HashSet<IMethodSymbol> expanded, ref int budget, int depth)
    {
        var box = new Box { Label = label };
        if (limit > 0 && depth >= limit)
            return box;

        var node = graph.NodeOf(method);
        if (node is null)
            return box;

        stack.Add(method);
        foreach (var branch in Groups(node, external))
        {
            if (budget-- <= 0)
                break;

            var (parts, expand) = Describe(branch, rootDirectory, limit, stack, expanded, depth);
            if (!expand)
            {
                box.Children.Add(new Box { Label = parts });
                continue;
            }

            var child = graph.NodeOf(branch.Target);
            if (child is not null && child.Outgoing.Count > 0)
                expanded.Add(branch.Target);

            box.Children.Add(Build(graph, branch.Target, parts, rootDirectory, limit, external, stack, expanded,
                ref budget, depth + 1));
        }

        stack.Remove(method);
        return box;
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
            parts.Add(new Segment(" [cycle]", Tone.Cycle));
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

    //Measure gives every subtree the width its widest row needs
    private static int Measure(Box box)
    {
        if (box.Children.Count == 0)
            return box.Width = box.LabelWidth;

        var total = 0;
        foreach (var child in box.Children)
            total += Measure(child);

        total += Gap * (box.Children.Count - 1);
        return box.Width = Math.Max(box.LabelWidth, total);
    }

    //Place writes one subtree into a character canvas, the label on its own row and the connector between rows
    private static void Place(Box box, int left, int row, int width, List<Cell[]> canvas)
    {
        box.Center = left + box.Width / 2;
        var line = Row(canvas, row, width);
        var column = box.Center - box.LabelWidth / 2;
        foreach (var segment in box.Label)
        {
            foreach (var character in segment.Text)
            {
                if (column >= 0 && column < line.Length)
                    line[column] = new Cell(character, segment.Tone);

                column++;
            }
        }

        if (box.Children.Count == 0)
            return;

        var total = box.Children.Sum(child => child.Width) + Gap * (box.Children.Count - 1);
        var childLeft = left + (box.Width - total) / 2;
        foreach (var child in box.Children)
        {
            Place(child, childLeft, row + 2, width, canvas);
            childLeft += child.Width + Gap;
        }

        Connector(box, row + 1, width, canvas);
    }

    //Connector draws the row between a parent and its children
    private static void Connector(Box box, int row, int width, List<Cell[]> canvas)
    {
        var line = Row(canvas, row, width);
        if (box.Children.Count == 1)
        {
            if (box.Center >= 0 && box.Center < line.Length)
                line[box.Center] = new Cell('│', Tone.Dim);

            return;
        }

        var first = box.Children[0].Center;
        var last = box.Children[^1].Center;

        for (var column = first; column <= last; column++)
        {
            if (column >= 0 && column < line.Length)
                line[column] = new Cell('─', Tone.Dim);
        }

        foreach (var child in box.Children)
        {
            if (child.Center >= 0 && child.Center < line.Length)
                line[child.Center] = new Cell('┬', Tone.Dim);
        }

        if (first >= 0 && first < line.Length)
            line[first] = new Cell('┌', Tone.Dim);

        if (last >= 0 && last < line.Length)
            line[last] = new Cell('┐', Tone.Dim);

        if (box.Center < 0 || box.Center >= line.Length)
            return;

        line[box.Center] = line[box.Center].Ch switch
        {
            '┬' => new Cell('┼', Tone.Dim),
            '┌' => new Cell('├', Tone.Dim),
            '┐' => new Cell('┤', Tone.Dim),
            _ => new Cell('┴', Tone.Dim),
        };
    }

    private static Cell[] Row(List<Cell[]> canvas, int row, int width)
    {
        while (canvas.Count <= row)
            canvas.Add(Enumerable.Repeat(new Cell(' ', Tone.Plain), width).ToArray());

        return canvas[row];
    }

    //Cells turns one canvas row into segments, merging hints of the same tone and dropping trailing space
    private static IEnumerable<Segment> Cells(Cell[] row)
    {
        var last = row.Length - 1;
        while (last >= 0 && row[last].Ch == ' ')
            last--;

        var column = 0;
        while (column <= last)
        {
            var tone = row[column].Tone;
            var text = new StringBuilder();
            while (column <= last && row[column].Tone == tone)
            {
                text.Append(row[column].Ch);
                column++;
            }

            yield return new Segment(text.ToString(), tone);
        }
    }

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

    private readonly record struct Cell(char Ch, Tone Tone);

    private sealed class Box
    {
        public required List<Segment> Label { get; init; }

        public List<Box> Children { get; } = new();

        public int Width { get; set; }

        public int Center { get; set; }

        public int LabelWidth => Label.Sum(segment => segment.Text.Length);
    }

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
