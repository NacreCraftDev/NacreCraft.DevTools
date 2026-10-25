using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace NacreCraft.ModBuild.Tools;

//FlowRenderer turns a class into an ordered outline of what runs, so the order is readable without reading the C#
//It is the structured view the call graph uses internally, exposed as its own command
internal static class FlowRenderer
{
    //MaxText caps one expression, since a long initializer would bury the order it belongs to
    private const int MaxText = 160;

    //MaxLines caps one method, so a generated body cannot fill the whole terminal
    private const int MaxLines = 4000;

    //MaxDepth is the safety limit when the caller asks for no limit
    private const int MaxDepth = 64;

    private const string Reset = "\u001b[0m";
    private const string DimColor = "\u001b[90m";
    private const string ClassColor = "\u001b[36m";
    private const string MethodColor = "\u001b[1;37m";
    private const string ControlColor = "\u001b[33m";
    private const string CallColor = "\u001b[32m";
    private const string ReturnColor = "\u001b[35m";
    private const string ErrorColor = "\u001b[31m";

    //Render the selected classes, returning an empty string and an error when the class name is unusable
    public static string Render(Compilation compilation, IReadOnlyCollection<string> sources, string root,
        string? className, bool all, int depth, bool color, out string error)
    {
        error = string.Empty;
        var classes = Classes(compilation, sources);

        if (className is not null)
        {
            var matches = classes.Where(type => Matches(type, className)).ToList();
            if (matches.Count == 0)
            {
                error = $"no class '{className}' in the selected sources";
                return string.Empty;
            }

            if (matches.Count > 1)
            {
                var names = string.Join(Environment.NewLine,
                    matches.Select(type => $"  {type.ToDisplayString()} ({Where(type, root)})"));
                error = $"class name is ambiguous, name one of these:{Environment.NewLine}{names}";
                return string.Empty;
            }

            classes = matches;
        }

        var text = new StringBuilder();
        text.AppendLine(Paint($"flow for {root}", ClassColor, color));
        text.AppendLine();
        if (classes.Count == 0)
        {
            text.AppendLine(Paint("  (no class)", DimColor, color));
            return text.ToString();
        }

        var limit = depth > 0 ? Math.Min(depth, MaxDepth) : MaxDepth;
        var expand = className is not null || all;
        foreach (var type in classes.OrderBy(type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            text.AppendLine(
                $"  {Paint(type.ToDisplayString(), ClassColor, color)} {Paint($"({Where(type, root)})", DimColor, color)}");
            var methods = Methods(type, sources);
            if (methods.Count == 0)
            {
                text.AppendLine(Paint("    (no method)", DimColor, color));
                text.AppendLine();
                continue;
            }

            foreach (var method in methods)
            {
                text.AppendLine($"    {Paint(Signature(method), MethodColor, color)}");
                if (!expand)
                    continue;

                var body = BodyOf(compilation, method);
                if (body is null)
                {
                    text.AppendLine(Paint("      (no body)", DimColor, color));
                    continue;
                }

                var state = new State(text, limit, color);
                RenderRoot(body, state);
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    //Paint wraps text in an ansi tone when colours are on, and leaves it alone for a file or a pipe
    private static string Paint(string text, string color, bool enabled)
        => enabled ? color + text + Reset : text;

    //Classes every type declared in the selected source files, nested types included
    private static List<INamedTypeSymbol> Classes(Compilation compilation, IReadOnlyCollection<string> sources)
    {
        var result = new List<INamedTypeSymbol>();
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        WalkNamespace(compilation.GlobalNamespace);
        return result;

        void WalkNamespace(INamespaceSymbol space)
        {
            foreach (var type in space.GetTypeMembers())
                WalkType(type);

            foreach (var nested in space.GetNamespaceMembers())
                WalkNamespace(nested);
        }

        void WalkType(INamedTypeSymbol type)
        {
            if (type.Locations.Any(location => location.IsInSource
                    && sources.Contains(location.SourceTree!.FilePath, StringComparer.OrdinalIgnoreCase))
                && seen.Add(type))
                result.Add(type);

            foreach (var nested in type.GetTypeMembers())
                WalkType(nested);
        }
    }

    //Matches accepts a short name, a full name, or the metadata spelling of a nested type
    private static bool Matches(INamedTypeSymbol type, string name)
    {
        var wanted = name.Replace('+', '.').Replace('/', '.');
        return type.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || type.ToDisplayString().Replace('+', '.').Replace('/', '.')
                .Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || type.MetadataName.Replace('+', '.').Equals(wanted, StringComparison.OrdinalIgnoreCase);
    }

    //Methods the method declarations of a type, accessors included, in source order
    private static List<IMethodSymbol> Methods(INamedTypeSymbol type, IReadOnlyCollection<string> sources)
    {
        var found = new List<IMethodSymbol>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol method when method.MethodKind != MethodKind.LocalFunction:
                    Add(method);
                    break;
                case IPropertySymbol property:
                    if (property.GetMethod is { } getter)
                        Add(getter);
                    if (property.SetMethod is { } setter)
                        Add(setter);
                    break;
                case IEventSymbol @event:
                    if (@event.AddMethod is { } add)
                        Add(add);
                    if (@event.RemoveMethod is { } remove)
                        Add(remove);
                    break;
            }
        }

        return found.OrderBy(PathOf, StringComparer.Ordinal).ThenBy(StartOf).ToList();

        void Add(IMethodSymbol method)
        {
            if (method.IsImplicitlyDeclared
                || !method.DeclaringSyntaxReferences.Any(reference =>
                    sources.Contains(reference.SyntaxTree.FilePath, StringComparer.OrdinalIgnoreCase)))
                return;

            if (seen.Add(method))
                found.Add(method);
        }
    }

    //Signature one method as a name, parameter list and the flags that change how it runs
    private static string Signature(IMethodSymbol method)
    {
        var name = method.MethodKind switch
        {
            MethodKind.Constructor => method.ContainingType.Name,
            MethodKind.StaticConstructor => "static " + method.ContainingType.Name,
            MethodKind.Destructor => "~" + method.ContainingType.Name,
            MethodKind.PropertyGet => $"{method.AssociatedSymbol?.Name}.get",
            MethodKind.PropertySet => $"{method.AssociatedSymbol?.Name}.set",
            MethodKind.EventAdd => $"{method.AssociatedSymbol?.Name}.add",
            MethodKind.EventRemove => $"{method.AssociatedSymbol?.Name}.remove",
            _ => method.Name,
        };

        var parameters = string.Join(", ", method.Parameters.Select(parameter =>
            $"{parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {parameter.Name}"));

        var returns = method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                or MethodKind.Destructor or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove
            ? string.Empty
            : " : " + method.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        var flags = new List<string> { AccessibilityWord(method.DeclaredAccessibility) };
        if (method.IsStatic)
            flags.Add("static");
        if (method.IsAsync)
            flags.Add("async");
        if (method.IsAbstract)
            flags.Add("abstract");
        if (method.IsVirtual)
            flags.Add("virtual");
        if (method.IsOverride)
            flags.Add("override");

        flags.RemoveAll(flag => flag.Length == 0);
        return $"{name}({parameters}){returns} [{string.Join(", ", flags)}]";
    }

    private static string AccessibilityWord(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Private => "private",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => string.Empty,
    };

    //BodyOf the operation tree behind a method declaration, null for an abstract or interface method
    private static IOperation? BodyOf(Compilation compilation, IMethodSymbol method)
    {
        var reference = method.DeclaringSyntaxReferences.FirstOrDefault();
        if (reference is null)
            return null;

        var node = reference.GetSyntax();
        var model = compilation.GetSemanticModel(node.SyntaxTree);
        var body = node switch
        {
            BaseMethodDeclarationSyntax declaration => declaration.Body ?? (SyntaxNode?)declaration.ExpressionBody?.Expression,
            AccessorDeclarationSyntax accessor => accessor.Body ?? (SyntaxNode?)accessor.ExpressionBody?.Expression,
            PropertyDeclarationSyntax property => property.ExpressionBody?.Expression,
            IndexerDeclarationSyntax indexer => indexer.ExpressionBody?.Expression,
            _ => null,
        };

        return body is null ? null : model.GetOperation(body);
    }

    private static string PathOf(IMethodSymbol method)
        => method.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath ?? string.Empty;

    private static int StartOf(IMethodSymbol method)
        => method.Locations.FirstOrDefault(location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue;

    private static string Where(INamedTypeSymbol type, string root)
    {
        var location = type.Locations.FirstOrDefault(item => item.IsInSource);
        if (location is null)
            return "?";

        var line = location.GetLineSpan().StartLinePosition.Line + 1;
        return $"{Path.GetRelativePath(root, location.SourceTree!.FilePath).Replace('\\', '/')}:{line}";
    }

    //RenderRoot a block as tree branches, or one expression body as its return value
    private static void RenderRoot(IOperation body, State state)
    {
        const string prefix = "      ";
        if (body is IBlockOperation block)
        {
            RenderOperations(block.Operations, state, 0, prefix);
            return;
        }

        WriteLine(state, prefix, true, "RETURN", Text(body));
    }

    private static void RenderOperations(IEnumerable<IOperation> operations, State state, int depth, string prefix,
        bool tail = false)
    {
        var list = operations.Where(operation => !operation.IsImplicit).ToList();
        for (var index = 0; index < list.Count; index++)
        {
            if (state.Stopped)
                return;

            if (depth > state.Limit)
            {
                WriteLine(state, prefix, true, "...", null);
                return;
            }

            Render(list[index], state, depth, prefix, !tail && index == list.Count - 1);
        }
    }

    private static void Render(IOperation operation, State state, int depth, string prefix, bool last)
    {
        switch (operation)
        {
            case IExpressionStatementOperation statement:
                RenderExpression(statement.Operation, state, prefix, last);
                break;
            case IVariableDeclarationGroupOperation group:
                RenderDeclarations(group, state, prefix, last);
                break;
            case IConditionalOperation conditional:
                RenderConditional(conditional, state, depth, prefix, last);
                break;
            case ISwitchOperation @switch:
                RenderSwitch(@switch, state, depth, prefix, last);
                break;
            case ILoopOperation loop:
                WriteLine(state, prefix, last, "LOOP", Header(loop.Syntax));
                RenderOperations(Operations(loop.Body), state, depth + 1, Child(prefix, last));
                break;
            case ITryOperation @try:
                RenderTry(@try, state, depth, prefix, last);
                break;
            case IReturnOperation @return:
                WriteLine(state, prefix, last, "RETURN", Text(@return.ReturnedValue));
                break;
            case IThrowOperation @throw:
                WriteLine(state, prefix, last, "THROW", Text(@throw.Exception));
                break;
            case IBranchOperation branch:
                WriteLine(state, prefix, last, branch.BranchKind.ToString().ToUpperInvariant(), null);
                break;
            case IUsingOperation @using:
                WriteLine(state, prefix, last, "USING", Text(@using.Resources));
                RenderOperations(Operations(@using.Body), state, depth + 1, Child(prefix, last));
                break;
            case IUsingDeclarationOperation usingDeclaration:
                WriteLine(state, prefix, last, usingDeclaration.IsAsynchronous ? "AWAIT USING" : "USING",
                    Text(usingDeclaration));
                break;
            case ILockOperation @lock:
                WriteLine(state, prefix, last, "LOCK", Text(@lock.LockedValue));
                RenderOperations(Operations(@lock.Body), state, depth + 1, Child(prefix, last));
                break;
            case ILocalFunctionOperation local:
                WriteLine(state, prefix, last, "LOCAL", local.Symbol.Name);
                if (local.Body is not null)
                    RenderOperations(local.Body.Operations, state, depth + 1, Child(prefix, last));
                break;
            case IBlockOperation block:
                RenderOperations(block.Operations, state, depth, prefix);
                break;
            case IEmptyOperation:
                break;
            default:
                RenderExpression(operation, state, prefix, last);
                break;
        }
    }

    //Operations the operations of a branch, a single statement being its own list
    private static IEnumerable<IOperation> Operations(IOperation operation)
        => operation is IBlockOperation block ? block.Operations : new[] { operation };

    //Child the prefix a nested block uses below one branch
    private static string Child(string prefix, bool last)
        => prefix + (last ? "    " : "│   ");

    private static void RenderDeclarations(IVariableDeclarationGroupOperation group, State state, string prefix,
        bool last)
    {
        var declarators = group.Declarations.SelectMany(declaration => declaration.Declarators).ToList();
        for (var index = 0; index < declarators.Count; index++)
        {
            var declarator = declarators[index];
            var value = declarator.Initializer?.Value;
            var lastOne = last && index == declarators.Count - 1;
            if (value is null)
            {
                WriteLine(state, prefix, lastOne, "DECLARE", declarator.Symbol.Name);
                continue;
            }

            WriteLine(state, prefix, lastOne, "SET", $"{declarator.Symbol.Name} = {Text(value)}");
        }
    }

    private static void RenderConditional(IConditionalOperation conditional, State state, int depth, string prefix,
        bool last)
    {
        var hasElse = conditional.WhenFalse is not null;
        WriteLine(state, prefix, hasElse ? false : last, "IF", Text(conditional.Condition));
        RenderOperations(Operations(conditional.WhenTrue), state, depth + 1,
            prefix + (hasElse ? "│   " : last ? "    " : "│   "), tail: hasElse);

        if (!hasElse)
            return;

        WriteLine(state, prefix, last, "ELSE", null);
        RenderOperations(Operations(conditional.WhenFalse!), state, depth + 1, Child(prefix, last));
    }

    private static void RenderSwitch(ISwitchOperation @switch, State state, int depth, string prefix, bool last)
    {
        WriteLine(state, prefix, last, "SWITCH", Text(@switch.Value));
        var child = Child(prefix, last);
        for (var index = 0; index < @switch.Cases.Length; index++)
        {
            var item = @switch.Cases[index];
            var lastCase = index == @switch.Cases.Length - 1;
            var label = item.Clauses.Length == 0
                ? "default"
                : string.Join(" / ", item.Clauses.Select(clause => Text(clause.Syntax)));
            WriteLine(state, child, lastCase, "CASE", label);
            RenderOperations(item.Body, state, depth + 1, Child(child, lastCase));
        }
    }

    private static void RenderTry(ITryOperation @try, State state, int depth, string prefix, bool last)
    {
        var hasTail = @try.Catches.Length > 0 || @try.Finally is not null;
        WriteLine(state, prefix, last, "TRY", null);
        var child = Child(prefix, last);
        RenderOperations(Operations(@try.Body), state, depth + 1, child, tail: hasTail);

        for (var index = 0; index < @try.Catches.Length; index++)
        {
            var handler = @try.Catches[index];
            var lastCatch = index == @try.Catches.Length - 1 && @try.Finally is null;
            WriteLine(state, child, lastCatch, "CATCH",
                handler.ExceptionType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            RenderOperations(Operations(handler.Handler), state, depth + 2, Child(child, lastCatch));
        }

        if (@try.Finally is null)
            return;

        WriteLine(state, child, true, "FINALLY", null);
        RenderOperations(Operations(@try.Finally), state, depth + 2, child + "    ");
    }

    private static void RenderExpression(IOperation operation, State state, string prefix, bool last)
    {
        var label = operation switch
        {
            IAwaitOperation => "AWAIT",
            IInvocationOperation => "CALL",
            IObjectCreationOperation => "NEW",
            ISimpleAssignmentOperation or ICompoundAssignmentOperation or ICoalesceAssignmentOperation
                or IIncrementOrDecrementOperation => "SET",
            IInvalidOperation invalid => InvalidLabel(invalid),
            _ => "EXPR",
        };

        WriteLine(state, prefix, last, label, Text(operation));
    }

    //InvalidLabel keeps an unresolved call readable, since a missing using is not a reason to hide the order
    private static string InvalidLabel(IInvalidOperation operation) => operation.Syntax switch
    {
        InvocationExpressionSyntax => "CALL",
        AssignmentExpressionSyntax => "SET",
        ObjectCreationExpressionSyntax => "NEW",
        AwaitExpressionSyntax => "AWAIT",
        _ => "EXPR",
    };

    //LabelColor the ansi tone of one step label, empty for a label that needs no emphasis
    private static string LabelColor(string label) => label switch
    {
        "IF" or "ELSE" or "LOOP" or "SWITCH" or "CASE" or "TRY" or "CATCH" or "FINALLY"
            or "USING" or "AWAIT USING" or "LOCK" or "LOCAL" or "BREAK" or "CONTINUE" => ControlColor,
        "CALL" or "NEW" or "AWAIT" => CallColor,
        "SET" or "DECLARE" or "EXPR" => DimColor,
        "RETURN" => ReturnColor,
        "THROW" => ErrorColor,
        _ => string.Empty,
    };

    private static void WriteLine(State state, string prefix, bool last, string label, string? detail)
    {
        if (state.Stopped)
            return;

        if (state.Lines >= MaxLines)
        {
            state.Stopped = true;
            state.Text.AppendLine(Paint("      ... line limit reached", DimColor, state.Color));
            return;
        }

        if (state.Color)
        {
            state.Text.Append(DimColor).Append(prefix).Append(last ? "└─ " : "├─ ").Append(Reset);
            var tone = LabelColor(label);
            if (tone.Length > 0)
                state.Text.Append(tone);
            state.Text.Append(label);
            if (tone.Length > 0)
                state.Text.Append(Reset);
        }
        else
        {
            state.Text.Append(prefix).Append(last ? "└─ " : "├─ ").Append(label);
        }

        if (!string.IsNullOrEmpty(detail))
            state.Text.Append(' ').Append(detail);
        state.Text.AppendLine();
        state.Lines++;
    }

    private static string Text(IOperation? operation)
        => operation is null ? string.Empty : Text(operation.Syntax);

    private static string Text(SyntaxNode node)
    {
        var text = node.ToString().Replace('\r', ' ').Replace('\n', ' ');
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);

        text = text.Replace(" .", ".").Replace("( ", "(").Replace(" )", ")").Trim();
        return text.Length <= MaxText ? text : text[..MaxText] + "...";
    }

    private static string Header(SyntaxNode syntax)
    {
        var text = Text(syntax);
        var brace = text.IndexOf('{');
        return brace >= 0 ? text[..brace].Trim() : text;
    }

    private sealed class State(StringBuilder text, int limit, bool color)
    {
        public StringBuilder Text { get; } = text;

        public int Limit { get; } = limit;

        public bool Color { get; } = color;

        public int Lines { get; set; }

        public bool Stopped { get; set; }
    }
}
