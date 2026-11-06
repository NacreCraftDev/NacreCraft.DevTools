using System.Text;
using System.Text.Json;
using FastParse;

namespace NacreCraft.Pit.Experimental.Java;

//JavaGraphBuilder turns a set of Java files into a JavaCallGraph through FastParser
//Each file is parsed and queried in parallel, then the calls are resolved once every method is known,
//because a call site carries a name rather than a symbol
internal static class JavaGraphBuilder
{
    //one query collects every declaration and call site, so a file is parsed once
    private const string Query = """
        (class_declaration name: (identifier) @class.name superclass: (superclass (type_identifier) @class.super)?) @class
        (interface_declaration name: (identifier) @iface.name) @iface
        (enum_declaration name: (identifier) @enum.name) @enum
        (record_declaration name: (identifier) @record.name) @record
        (method_declaration name: (identifier) @method.name parameters: (formal_parameters) @method.params body: (block) @method.body) @method
        (constructor_declaration name: (identifier) @ctor.name parameters: (formal_parameters) @ctor.params body: (constructor_body) @ctor.body) @ctor
        (method_invocation name: (identifier) @call.name arguments: (argument_list) @call.args) @call
        (object_creation_expression type: (_) @new.type arguments: (argument_list)? @new.args) @new
        (method_reference) @ref
        """;

    private static readonly QueryOptions Options = new()
    {
        Language = "java",
        Format = FastParseFormat.Json,
        Fields = FastParseField.CaptureName | FastParseField.Text |
                 FastParseField.ByteRange | FastParseField.Range | FastParseField.ChildCount,
    };

    public static JavaCallGraph Build(string rootDirectory, IReadOnlyList<string> files, Action<string>? parse,
        Action<string>? collect, out int skipped)
    {
        var facts = new FileFacts?[files.Count];
        var failed = 0;
        var gate = new object();

        //parsing is the slow part and a client belongs to one worker thread, so each thread keeps its own
        Parallel.For(0, files.Count,
            () => new FastParseClient(),
            (index, _, client) =>
            {
                var relative = Path.GetRelativePath(rootDirectory, files[index]).Replace('\\', '/');
                if (parse is not null)
                {
                    //the progress line is single threaded, so the callbacks take turns
                    lock (gate)
                        parse(relative);
                }

                try
                {
                    facts[index] = ReadFacts(client, files[index], relative);
                }
                catch (Exception)
                {
                    //the native parser can fail on one file, and the rest of the tree is still worth printing
                    facts[index] = null;
                }

                if (facts[index] is null)
                    Interlocked.Increment(ref failed);

                return client;
            },
            client => client.Dispose());

        var graph = new JavaCallGraph();
        var pending = new List<MethodFacts>();

        foreach (var file in facts)
        {
            if (file is null)
                continue;

            foreach (var type in file.Types)
            {
                graph.AddType(type.Full, type.Display, file.Path, type.Line);
                if (type.Super is { Length: > 0 } super)
                    graph.SetSuper(type.Full, super);
            }

            pending.AddRange(file.Methods.Where(method => method.OwnerType.Length > 0));
        }

        //every method is registered before any call is resolved, since a call may name a method declared further on
        var added = new List<(JavaCallGraph.Method Node, MethodFacts Facts)>();
        foreach (var method in pending)
        {
            collect?.Invoke($"{method.OwnerDisplay}.{method.Name}");

            var node = graph.AddMethod(method.OwnerType, method.Name, method.Arity, method.File, method.Line,
                method.IsPrivate, method.IsStatic);
            added.Add((node, method));
        }

        foreach (var (node, method) in added)
        {
            foreach (var call in method.Calls)
                ResolveCall(graph, node, method, call.Receiver, call.Name, call.Arity, call.Line);

            foreach (var created in method.News)
            {
                var type = graph.TypeOf(created.Type) ?? created.Type;
                var target = graph.Constructor(type, created.Arity) ?? graph.External(type, "<init>", created.Arity);
                graph.Connect(node, target, created.Line);
            }

            foreach (var reference in method.Refs)
                ResolveCall(graph, node, method, reference.Receiver, reference.Name, -1, reference.Line);
        }

        skipped = failed;
        return graph;
    }

    //ReadFacts parses one file and shapes its declarations and call sites around the byte ranges the query returns
    private static FileFacts? ReadFacts(FastParseClient client, string path, string relative)
    {
        var bytes = File.ReadAllBytes(path);
        var result = client.QueryBytes(bytes, Query, Options);
        if (result.Data.Length == 0)
            return null;

        using var document = result.JsonDocument();
        var root = document.RootElement;
        if (!root.TryGetProperty("matches", out var matches))
            return null;

        //the package is not part of the query, so it is read from the first line that declares one
        var package = PackageOf(bytes);

        var types = new List<TypeFact>();
        var methods = new List<MethodFacts>();

        foreach (var match in matches.EnumerateArray())
        {
            var captures = Captures(match, bytes);
            if (captures.TryGetValue("class", out var declared) && captures.TryGetValue("class.name", out var name))
            {
                types.Add(new TypeFact(name.Text, declared.Start, declared.End, name.Line,
                    captures.TryGetValue("class.super", out var super) ? super.Text : null));
                continue;
            }

            if (captures.TryGetValue("iface", out var contract) && captures.TryGetValue("iface.name", out var ifaceName))
            {
                types.Add(new TypeFact(ifaceName.Text, contract.Start, contract.End, ifaceName.Line, null));
                continue;
            }

            if (captures.TryGetValue("enum", out var enumeration) && captures.TryGetValue("enum.name", out var enumName))
            {
                types.Add(new TypeFact(enumName.Text, enumeration.Start, enumeration.End, enumName.Line, null));
                continue;
            }

            if (captures.TryGetValue("record", out var record) && captures.TryGetValue("record.name", out var recordName))
            {
                types.Add(new TypeFact(recordName.Text, record.Start, record.End, recordName.Line, null));
                continue;
            }

            if (captures.TryGetValue("method", out var method) && captures.TryGetValue("method.name", out var methodName))
            {
                var body = captures["method.body"];
                var parameters = captures["method.params"];
                methods.Add(new MethodFacts(
                    methodName.Text,
                    Arity(parameters),
                    method.Start,
                    body.Start,
                    body.End,
                    methodName.Line,
                    Modifiers(bytes, method.Start, methodName.Start),
                    relative));
                continue;
            }

            if (captures.TryGetValue("ctor", out var ctor) && captures.TryGetValue("ctor.name", out var ctorName))
            {
                var body = captures["ctor.body"];
                var parameters = captures["ctor.params"];
                methods.Add(new MethodFacts(
                    "<init>",
                    Arity(parameters),
                    ctor.Start,
                    body.Start,
                    body.End,
                    ctorName.Line,
                    Modifiers(bytes, ctor.Start, ctorName.Start),
                    relative));
            }
        }

        //a package-info file declares no type and is not a failure, so it simply contributes nothing
        if (types.Count == 0)
            return new FileFacts(relative, package, types, methods);

        NameTypes(types, package);

        //a method belongs to the innermost type whose range contains its declaration
        foreach (var method in methods)
        {
            var owner = types
                .Where(type => type.Start <= method.Start && type.End >= method.BodyEnd)
                .OrderBy(type => type.End - type.Start)
                .FirstOrDefault();
            if (owner is null)
                continue;

            method.OwnerType = owner.Full;
            method.OwnerDisplay = owner.Display;
            method.OwnerSuper = owner.Super;
        }

        var facts = new FileFacts(relative, package, types, methods);

        //the second pass attaches every call to the innermost method whose body contains it
        foreach (var match in matches.EnumerateArray())
        {
            var captures = Captures(match, bytes);

            if (captures.TryGetValue("call", out var call) && captures.TryGetValue("call.name", out var callName))
            {
                var owner = Owner(methods, call.Start, call.End);
                if (owner is null)
                    continue;

                owner.Calls.Add(new CallFact(
                    Receiver(bytes, call.Start, callName.Start),
                    callName.Text,
                    Arity(captures.TryGetValue("call.args", out var args) ? args : default),
                    callName.Line));
                continue;
            }

            if (captures.TryGetValue("new", out var created) && captures.TryGetValue("new.type", out var newType))
            {
                var owner = Owner(methods, created.Start, created.End);
                if (owner is null)
                    continue;

                owner.News.Add(new NewFact(
                    TypeText(newType.Text),
                    Arity(captures.TryGetValue("new.args", out var args) ? args : default),
                    newType.Line));
                continue;
            }

            if (captures.TryGetValue("ref", out var reference))
            {
                var owner = Owner(methods, reference.Start, reference.End);
                if (owner is null)
                    continue;

                var split = reference.Text.Split("::", 2);
                if (split.Length != 2)
                    continue;

                owner.Refs.Add(new RefFact(TrimReceiver(split[0]), split[1], reference.Line));
            }
        }

        return facts;
    }

    //Captures maps each capture name of one match to its text and position
    private static Dictionary<string, Capture> Captures(JsonElement match, byte[] bytes)
    {
        var captures = new Dictionary<string, Capture>(StringComparer.Ordinal);
        if (!match.TryGetProperty("captures", out var values))
            return captures;

        foreach (var capture in values.EnumerateArray())
        {
            if (!capture.TryGetProperty("name", out var name))
                continue;

            var key = name.GetString() ?? string.Empty;
            captures[key] = new Capture(
                capture.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                capture.TryGetProperty("startByte", out var start) ? start.GetInt32() : 0,
                capture.TryGetProperty("endByte", out var end) ? end.GetInt32() : 0,
                capture.TryGetProperty("startLine", out var line) ? line.GetInt32() : 0,
                capture.TryGetProperty("childCount", out var children) ? children.GetInt32() : 0);
        }

        return captures;
    }

    //Arity turns an argument or parameter list into how many entries it holds, since every entry adds two children around one separator
    private static int Arity(Capture list)
        => list.Children <= 2 ? 0 : (list.Children - 1) / 2;

    //Modifiers reads the words between the start of a declaration and its name, which is where private and static sit
    private static (bool Private, bool Static) Modifiers(byte[] bytes, int start, int name)
    {
        if (name <= start)
            return (false, false);

        var text = Encoding.UTF8.GetString(bytes, start, name - start);
        return (text.Contains("private", StringComparison.Ordinal), text.Contains("static", StringComparison.Ordinal));
    }

    //Receiver takes the text between the start of a call and its name, which is the expression the call runs on
    private static string Receiver(byte[] bytes, int start, int name)
        => name <= start ? string.Empty : TrimReceiver(Encoding.UTF8.GetString(bytes, start, name - start));

    private static string TrimReceiver(string text)
        => text.TrimEnd('.', ' ', '\t', '\r', '\n');

    //TypeText drops the generic arguments a written type may carry, so the name matches the declared one
    private static string TypeText(string text)
    {
        var generic = text.IndexOf('<');
        var name = generic > 0 ? text[..generic] : text;
        return name.Trim();
    }

    //Owner finds the innermost method whose body contains the range
    private static MethodFacts? Owner(List<MethodFacts> methods, int start, int end)
    {
        MethodFacts? best = null;
        foreach (var method in methods)
        {
            if (start < method.BodyStart || end > method.BodyEnd)
                continue;

            if (best is null || method.BodyEnd - method.BodyStart < best.BodyEnd - best.BodyStart)
                best = method;
        }

        return best;
    }

    //NameTypes gives each type its fully qualified name and display name, nesting under the smallest type that contains it
    //the widest range is named first, so a nested type already has its parent's display when it is assigned
    private static void NameTypes(List<TypeFact> types, string package)
    {
        foreach (var type in types.OrderByDescending(item => item.End - item.Start))
        {
            var parent = types
                .Where(item => !ReferenceEquals(item, type)
                    && item.Start <= type.Start && item.End >= type.End)
                .OrderBy(item => item.End - item.Start)
                .FirstOrDefault();

            type.Display = parent is null ? type.Name : $"{parent.Display}.{type.Name}";
            type.Full = package.Length == 0 ? type.Display : $"{package}.{type.Display}";
            type.Super = type.Super is { Length: > 0 } super ? Qualify(super, package) : null;
        }
    }

    private static string Qualify(string name, string package)
        => name.Contains('.') || package.Length == 0 ? name : $"{package}.{name}";

    //PackageOf reads the package declaration, which the query does not need for the call graph itself
    private static string PackageOf(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        const string keyword = "package ";
        var at = text.IndexOf(keyword, StringComparison.Ordinal);
        if (at < 0)
            return string.Empty;

        var end = text.IndexOf(';', at);
        return end < 0 ? string.Empty : text[(at + keyword.Length)..end].Trim();
    }

    private static void ResolveCall(JavaCallGraph graph, JavaCallGraph.Method caller, MethodFacts context,
        string receiver, string name, int arity, int line)
    {
        var type = ReceiverType(graph, context, receiver);
        if (type is not null)
        {
            if (AlongChain(graph, caller, type, name, arity, line))
                return;

            graph.Connect(caller, graph.External(type, name, arity), line);
            return;
        }

        if (AlongChain(graph, caller, context.OwnerType, name, arity, line))
            return;

        var candidates = graph.Candidates(name, arity);
        if (candidates.Count == 1)
        {
            graph.Connect(caller, candidates[0], line);
            return;
        }

        if (candidates.Count > 1)
        {
            var nearby = candidates.Where(candidate => SamePackage(candidate.Type, context.OwnerType)).ToList();
            if (nearby.Count == 1)
            {
                graph.Connect(caller, nearby[0], line);
                return;
            }
        }

        graph.Unresolved++;
    }

    //AlongChain looks for the method on a type and its superclasses, falling back to any overload when the count is unknown
    private static bool AlongChain(JavaCallGraph graph, JavaCallGraph.Method caller, string? type, string name, int arity,
        int line)
    {
        for (var current = type; current is not null; current = graph.SuperOf(current))
        {
            var hit = arity >= 0 ? graph.Find(current, name, arity) : null;
            hit ??= graph.FindAnyArity(current, name);
            if (hit is null)
                continue;

            graph.Connect(caller, hit, line);
            return true;
        }

        return false;
    }

    //ReceiverType answers the type a call runs on, null when the receiver is a local of unknown type
    private static string? ReceiverType(JavaCallGraph graph, MethodFacts context, string receiver)
    {
        if (receiver.Length == 0)
            return context.OwnerType;

        if (receiver == "this")
            return context.OwnerType;

        if (receiver == "super")
            return context.OwnerSuper;

        if (receiver.StartsWith("new ", StringComparison.Ordinal))
        {
            var created = TypeText(receiver[4..]);
            var createdDot = created.IndexOf('(');
            if (createdDot > 0)
                created = created[..createdDot];

            return graph.TypeOf(created) ?? created;
        }

        return graph.TypeOf(receiver) ?? ExternalName(receiver);
    }

    //ExternalName keeps a receiver that reads as a type, so a call into the jdk is reported as external instead of lost
    private static string? ExternalName(string name)
    {
        if (name.Length == 0)
            return null;

        return char.IsUpper(name[0]) || name.Contains('.') ? name : null;
    }

    private static bool SamePackage(string first, string second)
    {
        var left = first.LastIndexOf('.');
        var right = second.LastIndexOf('.');
        return left == right && (left == 0 || first[..left] == second[..right]);
    }

    private sealed class FileFacts
    {
        public FileFacts(string path, string package, List<TypeFact> types, List<MethodFacts> methods)
        {
            Path = path;
            Package = package;
            Types = types;
            Methods = methods;
        }

        public string Path { get; }
        public string Package { get; }
        public List<TypeFact> Types { get; }
        public List<MethodFacts> Methods { get; }
    }

    //TypeFact one declared type; Display drops the package, Full keeps it, and the range places it inside its parent
    private sealed class TypeFact
    {
        public TypeFact(string name, int start, int end, int line, string? super)
        {
            Name = name;
            Start = start;
            End = end;
            Line = line;
            Super = super;
        }

        public string Name { get; }
        public int Start { get; }
        public int End { get; }
        public int Line { get; }
        public string Display { get; set; } = string.Empty;
        public string Full { get; set; } = string.Empty;
        public string? Super { get; set; }
    }

    //MethodFacts one method or constructor with the call sites found in its body
    private sealed class MethodFacts
    {
        public MethodFacts(string name, int arity, int start, int bodyStart, int bodyEnd, int line,
            (bool Private, bool Static) modifiers, string file)
        {
            Name = name;
            Arity = arity;
            Start = start;
            BodyStart = bodyStart;
            BodyEnd = bodyEnd;
            Line = line;
            IsPrivate = modifiers.Private;
            IsStatic = modifiers.Static;
            File = file;
        }

        public string Name { get; }
        public int Arity { get; }
        public int Start { get; }
        public int BodyStart { get; }
        public int BodyEnd { get; }
        public int Line { get; }
        public bool IsPrivate { get; }
        public bool IsStatic { get; }
        public string File { get; }
        public string OwnerType { get; set; } = string.Empty;
        public string OwnerDisplay { get; set; } = string.Empty;
        public string? OwnerSuper { get; set; }
        public List<CallFact> Calls { get; } = new();
        public List<NewFact> News { get; } = new();
        public List<RefFact> Refs { get; } = new();
    }

    private readonly record struct CallFact(string Receiver, string Name, int Arity, int Line);

    private readonly record struct NewFact(string Type, int Arity, int Line);

    private readonly record struct RefFact(string Receiver, string Name, int Line);

    private readonly record struct Capture(string Text, int Start, int End, int Line, int Children);
}
