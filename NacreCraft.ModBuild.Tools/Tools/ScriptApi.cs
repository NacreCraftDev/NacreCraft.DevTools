using Jint;
using Jint.Native;
using Jint.Runtime;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.ModBuild.Tools;

//ScriptApi builds the javascript environment and answers completion and syntax questions about a script
//The language server uses it so the page completes real runtime members instead of a fixed name list
public static class ScriptApi
{
    public static Engine Create(NcProject project, IReadOnlyDictionary<string, string> variables, string root,
        bool allowClr)
    {
        var engine = new Engine(options =>
        {
            options
                .MaxStatements(10_000_000)
                .LimitMemory(256_000_000)
                .LimitRecursion(128)
                .TimeoutInterval(TimeSpan.FromSeconds(30));

            //off by default, since a script would otherwise reach every file and process the tool can
            if (allowClr)
                options.AllowClr();
        });

        var fs = new ScriptFs(root);
        engine.SetValue("log", new ScriptLog());
        engine.SetValue("project", new ScriptProject(project, variables, root));
        engine.SetValue("fs", fs);
        engine.SetValue("path", new ScriptPath(root));
        engine.SetValue("env", new ScriptEnv());
        engine.SetValue("hash", new ScriptHash(fs));
        engine.SetValue("xml", new ScriptXml());
        engine.SetValue("syntax", new ScriptSyntax(root, engine));
        return engine;
    }

    //Bindings maps a variable declared in front of the cursor to the path it was assigned
    //Nothing runs: a variable that points at a known object is enough to list that object's members
    private static Dictionary<string, string[]> Bindings(string before)
    {
        var bindings = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var end = ParseablePrefix(before);
        if (end <= 0 || !TryParse(before[..end], out var script))
            return bindings;

        Collect(script, bindings);
        return bindings;
    }

    //Collect walks the tree for variable declarations, skipping function bodies that cannot be seen from outside
    private static void Collect(Acornima.Ast.Node node, Dictionary<string, string[]> bindings)
    {
        if (node is Acornima.Ast.VariableDeclaration declaration)
        {
            foreach (var declarator in declaration.Declarations)
            {
                if (declarator.Id is Acornima.Ast.Identifier id && declarator.Init is not null
                    && PathOf(declarator.Init, out var path))
                {
                    bindings[id.Name] = path;
                }
            }
        }

        //a local declared inside a function is not visible to the completion outside of it
        if (node is Acornima.Ast.FunctionDeclaration or Acornima.Ast.FunctionExpression
            or Acornima.Ast.ArrowFunctionExpression)
        {
            return;
        }

        foreach (var child in node.ChildNodes)
            Collect(child, bindings);
    }

    //PathOf turns identifier and member chains into the path completion walks on the real environment
    private static bool PathOf(Acornima.Ast.Node node, out string[] path)
    {
        switch (node)
        {
            case Acornima.Ast.Identifier identifier:
                path = [identifier.Name];
                return true;

            case Acornima.Ast.MemberExpression member when PropertyOf(member, out var name):
            {
                if (!PathOf(member.Object, out var head))
                    break;

                path = [.. head, name];
                return true;
            }

            case Acornima.Ast.ParenthesizedExpression parenthesized:
                return PathOf(parenthesized.Expression, out path);
        }

        path = [];
        return false;
    }

    private static bool PropertyOf(Acornima.Ast.MemberExpression member, out string name)
    {
        switch (member.Property)
        {
            case Acornima.Ast.Identifier identifier when !member.Computed:
                name = identifier.Name;
                return true;

            case Acornima.Ast.StringLiteral literal:
                name = literal.Value;
                return true;
        }

        name = string.Empty;
        return false;
    }

    //ResolveBindings replaces the head of a chain with what the variable was assigned, guarding against cycles
    private static void ResolveBindings(Dictionary<string, string[]> bindings, List<string> chain)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (chain.Count > 0 && bindings.TryGetValue(chain[0], out var path) && path.Length > 0)
        {
            if (!seen.Add(chain[0]))
                return;

            chain.RemoveAt(0);
            chain.InsertRange(0, path);
        }
    }

    //ParseablePrefix returns the longest leading part that parses, leaving the half typed tail out
    private static int ParseablePrefix(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        if (Parses(text))
            return text.Length;

        for (var index = text.Length - 1; index > 0; index--)
        {
            var current = text[index];
            if (current != ';' && current != '}' && current != '\n')
                continue;

            if (Parses(text[..(index + 1)]))
                return index + 1;
        }

        return 0;
    }

    private static bool Parses(string text) => TryParse(text, out _);

    private static bool TryParse(string text, out Acornima.Ast.Script script)
    {
        try
        {
            script = new Acornima.Parser().ParseScript(text, "completion");
            return true;
        }
        catch
        {
            script = null!;
            return false;
        }
    }

    //Complete lists the members a property chain reaches, or the globals when the cursor is on a bare word
    public static ScriptCompletionItem[] Complete(Engine engine, string source, int line, int column)
    {
        var before = SliceBefore(source, line, column);
        var tail = System.Text.RegularExpressions.Regex.Match(
            before,
            @"([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\.?$"
        );

        var expression = tail.Success ? tail.Groups[1].Value : string.Empty;
        var endsWithDot = before.EndsWith(".", StringComparison.Ordinal);
        var segments = expression.Length > 0
            ? expression.Split('.')
            : Array.Empty<string>();

        string prefix;
        var chain = new List<string>();
        if (endsWithDot)
        {
            prefix = string.Empty;
            chain.AddRange(segments);
        }
        else
        {
            prefix = segments.Length > 0 ? segments[^1] : string.Empty;
            chain.AddRange(segments.Take(Math.Max(0, segments.Length - 1)));
        }

        //a name declared earlier points at the real object, so the chain starts there instead of at the engine
        if (chain.Count > 0)
            ResolveBindings(Bindings(before), chain);

        var names = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<ScriptCompletionItem>();

        void Add(Jint.Native.Object.ObjectInstance? owner, string name)
        {
            if (!names.Add(name) || !name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length)
                return;

            var kind = owner is null ? "keyword" : "value";
            if (owner is not null)
            {
                //a getter may run arbitrary code, so completion only asks for the value when it is safe
                try
                {
                    var member = owner.Get(JsValue.FromObject(engine, name));
                    if (member.IsCallable())
                        kind = "method";
                    else if (member.IsObject())
                        kind = "object";
                    else if (member.IsString())
                        kind = "string";
                    else if (member.IsNumber())
                        kind = "number";
                    else if (member.IsBoolean())
                        kind = "boolean";
                }
                catch
                {
                    kind = "value";
                }
            }

            items.Add(new ScriptCompletionItem { name = name, kind = kind });
        }

        if (chain.Count == 0)
        {
            foreach (var key in engine.Global.GetOwnPropertyKeys())
            {
                if (key.IsString())
                    Add(engine.Global, key.AsString());
            }

            foreach (var keyword in Keywords)
                Add(null, keyword);
        }
        else
        {
            var value = engine.GetValue(chain[0]);
            for (var index = 1; index < chain.Count; index++)
            {
                if (value is Jint.Native.Object.ObjectInstance instance)
                    value = instance.Get(chain[index]);
                else
                {
                    value = JsValue.Undefined;
                    break;
                }
            }

            if (value is Jint.Native.Object.ObjectInstance target)
            {
                foreach (var key in target.GetOwnPropertyKeys())
                {
                    if (key.IsString())
                        Add(target, key.AsString());
                }
            }
        }

        return items
            .OrderBy(item => item.name, StringComparer.Ordinal)
            .Take(200)
            .ToArray();
    }

    //Check parses the whole script and reports the first syntax error
    public static ScriptDiagnostic? Check(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        try
        {
            Engine.PrepareScript(source, "script");
            return null;
        }
        catch (ScriptPreparationException error) when (error.InnerException is Acornima.ParseErrorException parse)
        {
            return new ScriptDiagnostic
            {
                line = Math.Max(1, parse.LineNumber),
                column = Math.Max(1, parse.Column + 1),
                message = parse.Message,
            };
        }
        catch (Acornima.ParseErrorException error)
        {
            return new ScriptDiagnostic
            {
                line = Math.Max(1, error.LineNumber),
                column = Math.Max(1, error.Column + 1),
                message = error.Message,
            };
        }
        catch (Exception error)
        {
            return new ScriptDiagnostic
            {
                line = 1,
                column = 1,
                message = error.Message,
            };
        }
    }

    private static string SliceBefore(string source, int line, int column)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var index = 0;
        for (var row = 0; row < line - 1 && row < lines.Length; row++)
            index += lines[row].Length + 1;

        index = Math.Clamp(index + Math.Max(0, column - 1), 0, source.Length);
        return source[..index];
    }

    private static readonly string[] Keywords =
    [
        "var", "let", "const", "function", "return", "if", "else", "for", "while", "do",
        "switch", "case", "break", "continue", "new", "delete", "typeof", "instanceof",
        "in", "of", "this", "true", "false", "null", "undefined", "throw", "try", "catch",
        "finally", "class", "extends", "super", "yield", "await", "async", "void",
        "import", "export", "from", "as",
    ];
}

//ScriptCompletionItem one name the script may type next
public sealed class ScriptCompletionItem
{
    public string name { get; init; } = string.Empty;

    public string kind { get; init; } = "value";
}

//ScriptDiagnostic one syntax error with its position
public sealed class ScriptDiagnostic
{
    public int line { get; init; }

    public int column { get; init; }

    public string message { get; init; } = string.Empty;
}
