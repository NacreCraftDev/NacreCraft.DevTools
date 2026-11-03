using System.Text;
using System.Text.Json;
using FastParse;

namespace NacreCraft.ModBuild.Tools.Experimental.Java;

//JavaFlowSource reads Java files into the class and method shapes the flow outline prints
//FastParser returns a flat node list with parent ids, which is rebuilt into a tree before the members are picked out
internal static class JavaFlowSource
{
    //MaxText caps one rendered snippet, since a long expression would bury the order it belongs to
    private const int MaxText = 200;

    private static readonly ParseOptions Options = new()
    {
        Language = "java",
        Format = FastParseFormat.Json,
        Fields = FastParseField.Id | FastParseField.ParentId | FastParseField.Rule |
                 FastParseField.Text | FastParseField.Range,
    };

    //Read parses the files in parallel and returns every declared type in declaration order
    //a body is kept only when it can be printed, so a large tree that is not expanded costs signatures alone
    public static List<JavaTypeFlow> Read(IReadOnlyList<string> files, string root, string? className, bool all,
        Action<string>? progress, out int skipped)
    {
        var perFile = new List<JavaTypeFlow>?[files.Count];
        var failed = 0;
        var gate = new object();

        Parallel.For(0, files.Count,
            () => new FastParseClient(),
            (index, _, client) =>
            {
                var relative = Path.GetRelativePath(root, files[index]).Replace('\\', '/');
                if (progress is not null)
                {
                    lock (gate)
                        progress(relative);
                }

                try
                {
                    perFile[index] = ReadFile(client, files[index], relative, className, all);
                }
                catch (Exception)
                {
                    //one file the parser cannot handle must not take the whole outline down
                    perFile[index] = null;
                }

                if (perFile[index] is null)
                    Interlocked.Increment(ref failed);

                return client;
            },
            client => client.Dispose());

        var types = new List<JavaTypeFlow>();
        foreach (var file in perFile)
        {
            if (file is not null)
                types.AddRange(file);
        }

        skipped = failed;
        return types;
    }

    private static List<JavaTypeFlow>? ReadFile(FastParseClient client, string path, string relative,
        string? className, bool all)
    {
        var bytes = File.ReadAllBytes(path);
        var result = client.ParseBytes(bytes, Options);
        if (result.Data.Length == 0)
            return null;

        using var document = result.JsonDocument();
        if (!document.RootElement.TryGetProperty("nodes", out var nodes))
            return null;

        var root = BuildTree(nodes);
        if (root is null)
            return null;

        var types = new List<JavaTypeFlow>();
        Collect(root, string.Empty, relative, className, all, types);
        return types;
    }

    //BuildTree rebuilds the parent and child links, since the native side returns a flat list
    private static JavaFlowNode? BuildTree(JsonElement nodes)
    {
        var flat = new List<Flat>();
        foreach (var node in nodes.EnumerateArray())
        {
            if (!node.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number)
                continue;

            flat.Add(new Flat(
                id.GetInt32(),
                node.TryGetProperty("parentId", out var parent) && parent.ValueKind == JsonValueKind.Number
                    ? parent.GetInt32()
                    : -1,
                node.TryGetProperty("rule", out var rule) ? rule.GetString() ?? string.Empty : string.Empty,
                node.TryGetProperty("text", out var text) ? Compact(text.GetString() ?? string.Empty) : string.Empty,
                node.TryGetProperty("startLine", out var line) && line.ValueKind == JsonValueKind.Number
                    ? line.GetInt32()
                    : 0));
        }

        JavaFlowNode? root = null;
        var built = new Dictionary<int, JavaFlowNode>(flat.Count);
        foreach (var item in flat)
        {
            var node = new JavaFlowNode(item.Rule, item.Text, item.Line);
            built[item.Id] = node;

            if (item.Parent >= 0 && built.TryGetValue(item.Parent, out var parent))
                parent.Children.Add(node);
            else
                root ??= node;
        }

        return root;
    }

    //Collect walks the tree and turns every type declaration into a JavaTypeFlow, nested types included
    private static void Collect(JavaFlowNode node, string outer, string file, string? className, bool all,
        List<JavaTypeFlow> types)
    {
        foreach (var child in node.Children)
        {
            if (TypeRules.Contains(child.Rule))
            {
                var name = NameOf(child);
                var display = outer.Length == 0 ? name : $"{outer}.{name}";
                var type = new JavaTypeFlow(display, file, child.Line);

                var wanted = all || Matches(display, name, className);
                var body = child.Children.FirstOrDefault(item => BodyRules.Contains(item.Rule));
                if (body is not null)
                {
                    //an enum keeps its methods in a declarations node after the constants, so that layer is entered
                    var members = body.Children.SelectMany(member =>
                        member.Rule == "enum_body_declarations"
                            ? (IEnumerable<JavaFlowNode>)member.Children
                            : new[] { member });

                    foreach (var member in members)
                    {
                        if (member.Rule is not ("method_declaration" or "constructor_declaration"))
                            continue;

                        var method = new JavaMethodFlow(Signature(member), wanted ? BodyOf(member) : null);
                        type.Methods.Add(method);
                    }
                }

                types.Add(type);
                Collect(child, display, file, className, all, types);
                continue;
            }

            Collect(child, outer, file, className, all, types);
        }
    }

    //NameOf reads the identifier that follows the modifiers, which is the declared type name
    private static string NameOf(JavaFlowNode type)
        => type.Children.FirstOrDefault(child => child.Rule == "identifier")?.Text ?? "?";

    private static bool Matches(string display, string name, string? wanted)
    {
        if (wanted is null)
            return false;

        return name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || display.Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || display.Replace('.', '/').Equals(wanted.Replace('.', '/'), StringComparison.OrdinalIgnoreCase);
    }

    //Signature keeps the declaration up to the body or the semicolon, which is how Java spells a method
    private static string Signature(JavaFlowNode method)
    {
        var text = method.Text;
        var brace = text.IndexOf('{');
        var semicolon = text.IndexOf(';');
        var cut = brace < 0 ? semicolon : semicolon < 0 ? brace : Math.Min(brace, semicolon);
        return cut < 0 ? text : text[..cut].TrimEnd();
    }

    private static JavaFlowNode? BodyOf(JavaFlowNode method)
        => method.Children.FirstOrDefault(child => child.Rule is "block" or "constructor_body");

    //Compact folds whitespace into single spaces and caps the length, since the outline prints snippets
    private static string Compact(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, MaxText + 3));
        var space = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(character);
            if (builder.Length > MaxText)
            {
                builder.Append("...");
                break;
            }
        }

        return builder.ToString();
    }

    private static readonly HashSet<string> TypeRules = new(StringComparer.Ordinal)
    {
        "class_declaration",
        "interface_declaration",
        "enum_declaration",
        "record_declaration",
    };

    private static readonly HashSet<string> BodyRules = new(StringComparer.Ordinal)
    {
        "class_body",
        "interface_body",
        "enum_body",
        "record_body",
    };

    private readonly record struct Flat(int Id, int Parent, string Rule, string Text, int Line);
}

//JavaFlowNode one syntax node kept for the outline; the text is already folded and capped
internal sealed class JavaFlowNode
{
    public JavaFlowNode(string rule, string text, int line)
    {
        Rule = rule;
        Text = text;
        Line = line;
    }

    public string Rule { get; }

    public string Text { get; }

    public int Line { get; }

    public List<JavaFlowNode> Children { get; } = new();
}

//JavaTypeFlow one declared type and the methods the outline lists
internal sealed class JavaTypeFlow
{
    public JavaTypeFlow(string display, string file, int line)
    {
        Display = display;
        File = file;
        Line = line;
    }

    public string Display { get; }

    public string File { get; }

    public int Line { get; }

    public List<JavaMethodFlow> Methods { get; } = new();
}

//JavaMethodFlow one method or constructor; Body is null when the outline only lists signatures
internal sealed class JavaMethodFlow
{
    public JavaMethodFlow(string signature, JavaFlowNode? body)
    {
        Signature = signature;
        Body = body;
    }

    public string Signature { get; }

    public JavaFlowNode? Body { get; }
}
