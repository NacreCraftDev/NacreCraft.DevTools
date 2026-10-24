using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace NacreCraft.ModBuild.Diagnostics;

//CallGraph is the reusable call graph behind both the callgraph command and the NC0004 cycle check
//A node is a method and an edge is one call site, marked when the call only runs under a condition
internal sealed class CallGraph
{
    //One call from a method to another; CycleEdge is false for a call that can never hold a cycle
    internal sealed record Edge(IMethodSymbol Target, Location Site, bool Conditional, bool CycleEdge, string? Condition);

    internal sealed class Node
    {
        public Node(IMethodSymbol method) => Method = method;

        public IMethodSymbol Method { get; }

        public List<Edge> Outgoing { get; } = new();

        //How many calls reach this method, since a method nothing calls starts a tree
        public int Incoming { get; set; }
    }

    //How a recursive call moves the condition that keeps it going
    internal enum StepKind
    {
        Terminating,
        NonTerminating,
        Unknown,
    }

    //Shape of one call site, shared by the analyzer and the command line builder
    internal readonly record struct CallShape(bool Deferred, bool Looped, bool Conditional, StepKind Step,
        string? Condition);

    private readonly object _gate = new();
    private readonly Dictionary<IMethodSymbol, Node> _nodes = new(SymbolEqualityComparer.Default);
    private readonly HashSet<IMethodSymbol> _guarded = new(SymbolEqualityComparer.Default);

    public void Register(IMethodSymbol method)
    {
        lock (_gate)
            NodeLocked(Canonical(method));
    }

    public void AddEdge(IMethodSymbol caller, IMethodSymbol target, Location site, bool conditional, bool cycleEdge,
        string? condition)
    {
        lock (_gate)
        {
            var reached = Canonical(target);
            NodeLocked(Canonical(caller)).Outgoing.Add(new Edge(reached, site, conditional, cycleEdge, condition));
            NodeLocked(reached).Incoming++;
        }
    }

    public void MarkGuarded(IMethodSymbol method)
    {
        lock (_gate)
            _guarded.Add(Canonical(method));
    }

    public bool IsGuarded(IMethodSymbol method)
    {
        lock (_gate)
            return _guarded.Contains(Canonical(method));
    }

    public Node? NodeOf(IMethodSymbol method)
    {
        lock (_gate)
            return _nodes.TryGetValue(Canonical(method), out var node) ? node : null;
    }

    public IReadOnlyList<Node> Nodes()
    {
        lock (_gate)
            return _nodes.Values.ToArray();
    }

    private Node NodeLocked(IMethodSymbol method)
    {
        if (_nodes.TryGetValue(method, out var node))
            return node;

        node = new Node(method);
        _nodes[method] = node;
        return node;
    }

    //A cycle is reachable from every method on it, so the set of methods it passes through is its identity and the
    //shortest spelling is kept; walking from a sorted list afterwards keeps the output the same between runs
    public IReadOnlyList<CallCycle> Cycles()
    {
        var outgoing = new Dictionary<IMethodSymbol, Edge[]>(SymbolEqualityComparer.Default);
        HashSet<IMethodSymbol> guarded;
        Node[] nodes;

        lock (_gate)
        {
            nodes = _nodes.Values.ToArray();
            guarded = new HashSet<IMethodSymbol>(_guarded, SymbolEqualityComparer.Default);
            foreach (var node in nodes)
                outgoing[node.Method] = node.Outgoing.Where(edge => edge.CycleEdge).ToArray();
        }

        var cycles = new Dictionary<string, CallCycle>(StringComparer.Ordinal);
        foreach (var start in nodes.Select(node => node.Method).OrderBy(Name, StringComparer.Ordinal))
        {
            //a method that can leave early ends its own cycle, so the walk never has to start there
            if (guarded.Contains(start))
                continue;

            if (!outgoing.TryGetValue(start, out var edges) || edges.Length == 0)
                continue;

            var path = new List<Edge>();
            var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default) { start };
            if (!Find(start, start, outgoing, guarded, visited, path))
                continue;

            var chain = Chain(start, path);
            var key = string.Join(",", path.Select(edge => Signature(edge.Target))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal));

            if (cycles.TryGetValue(key, out var known) && string.CompareOrdinal(chain, known.Chain) >= 0)
                continue;

            cycles[key] = new CallCycle(chain, path[0].Site, path.Any(edge => edge.Conditional));
        }

        return cycles.Values.OrderBy(cycle => cycle.Chain, StringComparer.Ordinal).ToList();
    }

    private static bool Find(IMethodSymbol start, IMethodSymbol current,
        IReadOnlyDictionary<IMethodSymbol, Edge[]> outgoing, HashSet<IMethodSymbol> guarded,
        HashSet<IMethodSymbol> visited, List<Edge> path)
    {
        if (!outgoing.TryGetValue(current, out var edges))
            return false;

        foreach (var edge in edges)
        {
            if (SymbolEqualityComparer.Default.Equals(edge.Target, start))
            {
                path.Add(edge);
                return true;
            }

            //a method that can leave early or is already on this walk cannot be part of a new cycle
            if (guarded.Contains(edge.Target) || !visited.Add(edge.Target))
                continue;

            path.Add(edge);
            if (Find(start, edge.Target, outgoing, guarded, visited, path))
                return true;

            path.RemoveAt(path.Count - 1);
            visited.Remove(edge.Target);
        }

        return false;
    }

    private static string Chain(IMethodSymbol start, List<Edge> path)
    {
        var names = new List<string> { Name(start) };
        names.AddRange(path.Select(edge => Name(edge.Target)));
        return string.Join(" -> ", names);
    }

    //Name names a method the way the source writes it, accessors included
    public static string Name(IMethodSymbol method)
    {
        var name = method.AssociatedSymbol?.Name ?? method.Name;

        //constructors are named .ctor and .cctor in metadata, which would render as Type..ctor
        if (name is ".ctor" or ".cctor")
            name = name[1..];

        var type = method.ContainingType?.Name ?? string.Empty;
        return type.Length == 0 ? name : $"{type}.{name}";
    }

    //Signature the full method name, so two overloads on one cycle are not folded into the same key
    private static string Signature(IMethodSymbol method)
        => method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    //Canonical points a partial method at the half carrying the body, since a call names the definition while the
    //declaration that owns the edges is the implementation
    public static IMethodSymbol Canonical(IMethodSymbol method)
        => method.PartialImplementationPart ?? method;

    //Target the method a call reaches, constructors included, null for anything that is not a call
    public static IMethodSymbol? Target(IOperation operation) => operation switch
    {
        IInvocationOperation invocation => invocation.TargetMethod,
        IObjectCreationOperation creation => creation.Constructor,
        _ => null,
    };

    //Local whether a method is written in this project, which is what makes it part of a cycle the compiler can see
    public static bool Local(IMethodSymbol method)
        => method.ContainingType is { TypeKind: not TypeKind.Error }
            && method.Locations.Any(location => location.IsInSource);

    //Classify answers how a call site behaves: whether it runs later, in a loop, under a test, or moves away from a
    //recursive condition; the condition text is for the tree, the analyzer only reads the flags
    public static CallShape Classify(IOperation operation)
    {
        var deferred = Ancestor(operation, current => current is IAnonymousFunctionOperation or ILocalFunctionOperation);
        var looped = Ancestor(operation, current => current is ILoopOperation);
        var conditional = Ancestor(operation, IsCondition);
        var step = operation is IInvocationOperation invocation
            ? Progress(invocation)
            : conditional ? StepKind.Unknown : StepKind.NonTerminating;
        return new CallShape(deferred, looped, conditional, step, ConditionOf(operation));
    }

    //Guarded whether a return, throw or branch only happens on some paths, which is what makes a cycle end
    public static bool Guarded(IOperation operation)
        => Ancestor(operation, current => current
            is IAnonymousFunctionOperation or ILocalFunctionOperation or ILoopOperation
            || IsCondition(current));

    //HasExit whether a method body can leave early, judged the same way the analyzer judges one
    public static bool HasExit(IOperation body)
        => Walk(body).Any(operation => (operation is IReturnOperation or IThrowOperation or IBranchOperation)
            && Guarded(operation));

    //Walk lists the operations of a body without stepping into callbacks or local functions, which are their own nodes
    public static IEnumerable<IOperation> Walk(IOperation operation)
    {
        foreach (var child in operation.ChildOperations)
        {
            if (child is IAnonymousFunctionOperation or ILocalFunctionOperation)
                continue;

            yield return child;
            foreach (var nested in Walk(child))
                yield return nested;
        }
    }

    //IsCondition whether an operation only runs when a test says so
    private static bool IsCondition(IOperation operation)
        => operation is IConditionalOperation or ISwitchOperation or ISwitchExpressionOperation or ITryOperation
                or ICoalesceOperation or IConditionalAccessOperation
            || (operation is IBinaryOperation binary && binary.OperatorKind
                is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr);

    //Ancestor walks up from an operation looking for a shape around it
    private static bool Ancestor(IOperation operation, Func<IOperation, bool> match)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (match(current))
                return true;
        }

        return false;
    }

    //ConditionOf reads the nearest test around a call for the tree, null when the call always runs
    private static string? ConditionOf(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case IConditionalOperation conditional:
                    if (conditional.WhenTrue is { } yes && Contains(yes, operation))
                        return Short("if " + conditional.Condition.Syntax.ToString());

                    //a call in the test itself always runs, so nothing above it describes this call
                    return conditional.WhenFalse is { } no && Contains(no, operation) ? "else" : null;

                case ISwitchOperation:
                    return "switch case";

                case ISwitchExpressionOperation:
                    return "switch arm";

                case ICoalesceOperation:
                    return "??";

                case IConditionalAccessOperation:
                    return "?.";

                case ITryOperation:
                    return "try";

                case IBinaryOperation binary when binary.OperatorKind
                    is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr:
                    return binary.OperatorKind == BinaryOperatorKind.ConditionalAnd ? "&&" : "||";
            }
        }

        return null;
    }

    private static bool Contains(IOperation parent, IOperation child)
        => parent.DescendantsAndSelf().Contains(child);

    //Short collapses whitespace and caps a condition so one long test does not push the tree off the screen
    private static string Short(string text)
    {
        var compact = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 48 ? compact : compact[..45] + "...";
    }

    //Progress answers whether a recursive call moves the value its branch tests away from that branch
    //A call with no condition around it keeps going, a call under a condition it cannot read is left unknown, and only a
    //step against the direction the branch needs is taken as proof that the recursion ends
    private static StepKind Progress(IInvocationOperation invocation)
    {
        if (Wrapping(invocation) is not { } wrapping)
        {
            //a call behind ?., ??, && or a switch is guarded, but the guard does not name a step direction,
            //so the recursion cannot be proven endless from the source alone
            return Ancestor(invocation, IsProtection) ? StepKind.Unknown : StepKind.NonTerminating;
        }

        if (Direction(wrapping) is not { } check)
            return StepKind.Unknown;

        var matched = false;
        foreach (var argument in invocation.Arguments)
        {
            if (Step(argument.Value) is not { } step
                || !SymbolEqualityComparer.Default.Equals(step.Symbol, check.Symbol))
                continue;

            matched = true;
            if (step.Toward * check.Toward < 0)
                return StepKind.Terminating;
        }

        return matched ? StepKind.NonTerminating : StepKind.Unknown;
    }

    //IsProtection whether an operation guards a call without naming a numeric direction to leave by
    private static bool IsProtection(IOperation operation)
        => operation is IConditionalOperation or ISwitchOperation or ISwitchExpressionOperation
                or ICoalesceOperation or IConditionalAccessOperation
            || (operation is IBinaryOperation binary && binary.OperatorKind
                is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr);

    //Wrapping finds the innermost condition around a call, together with whether the call sits in the branch that runs when
    //it holds; null means no condition wraps it, or it sits in the test itself, which always runs
    private static (IConditionalOperation Node, bool WhenTrue)? Wrapping(IOperation operation)
    {
        for (var current = operation.Parent; current is not null; current = current.Parent)
        {
            if (current is not IConditionalOperation conditional)
                continue;

            //a branch may be wrapped in conversions and blocks of its own, so the call is looked for in the whole subtree
            if (conditional.WhenTrue is { } yes && yes.DescendantsAndSelf().Contains(operation))
                return (conditional, true);

            if (conditional.WhenFalse is { } no && no.DescendantsAndSelf().Contains(operation))
                return (conditional, false);

            return null;
        }

        return null;
    }

    //Direction reads which way the tested number has to move for the branch to stay on
    //`x > 0` in the true branch is kept by growing x, and the same test in the false branch by shrinking it, so the answer
    //flips with the branch and again with a surrounding not. A test that is not a comparison against a number gives none
    private static (ISymbol Symbol, int Toward)? Direction((IConditionalOperation Node, bool WhenTrue) wrapping)
    {
        IOperation condition = wrapping.Node.Condition;
        var negated = false;
        while (condition is IUnaryOperation unary && unary.OperatorKind == UnaryOperatorKind.Not)
        {
            negated = !negated;
            condition = unary.Operand;
        }

        if (condition is not IBinaryOperation binary)
            return null;

        //the constant may sit on either side, and a flipped comparison reads the other way round
        var symbol = SymbolOf(binary.LeftOperand);
        var kind = binary.OperatorKind;
        if (symbol is null)
        {
            symbol = SymbolOf(binary.RightOperand);
            kind = Flip(kind);
        }

        if (symbol is null)
            return null;

        var toward = kind switch
        {
            BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual => 1,
            BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual => -1,
            _ => 0,
        };

        if (toward == 0)
            return null;

        if (!wrapping.WhenTrue)
            toward = -toward;

        return (symbol, negated ? -toward : toward);
    }

    //Step reads a plain `x + k` or `x - k`, telling which way the value moves as the call repeats
    private static (ISymbol Symbol, int Toward)? Step(IOperation value)
    {
        if (value is not IBinaryOperation binary)
            return null;

        var symbol = SymbolOf(binary.LeftOperand);
        if (symbol is null || Number(binary.RightOperand) is not { } amount || amount == 0)
            return null;

        var toward = amount > 0 ? 1 : -1;
        return binary.OperatorKind switch
        {
            BinaryOperatorKind.Add => (symbol, toward),
            BinaryOperatorKind.Subtract => (symbol, -toward),
            _ => null,
        };
    }

    //SymbolOf the local, parameter, field or property an operation reads, null for anything else
    private static ISymbol? SymbolOf(IOperation operation) => operation switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        _ => null,
    };

    //Number the constant value an operation folds to, null when it does not fold to a number
    private static double? Number(IOperation operation)
    {
        if (!operation.ConstantValue.HasValue)
            return null;

        return operation.ConstantValue.Value switch
        {
            int value => value,
            long value => value,
            short value => value,
            byte value => value,
            uint value => value,
            ulong value => value,
            float value => value,
            double value => value,
            _ => null,
        };
    }

    //Flip turns a comparison around so the tested value reads on the left
    private static BinaryOperatorKind Flip(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.GreaterThan => BinaryOperatorKind.LessThan,
        BinaryOperatorKind.GreaterThanOrEqual => BinaryOperatorKind.LessThanOrEqual,
        BinaryOperatorKind.LessThan => BinaryOperatorKind.GreaterThan,
        BinaryOperatorKind.LessThanOrEqual => BinaryOperatorKind.GreaterThanOrEqual,
        _ => kind,
    };
}

//CallCycle a cycle with no way out, named by the path through it
internal sealed record CallCycle(string Chain, Location Site, bool Conditional);
