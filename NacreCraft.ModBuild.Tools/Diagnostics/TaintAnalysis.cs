using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace NacreCraft.ModBuild.Diagnostics;

//TaintAnalysis follows values from a root method to a sink, through locals, fields and local calls
//The cli only formats the result; the hook and memory checks call the same engine, which is what makes it a reusable tool
internal static class TaintAnalysis
{
    //MaxDepth is the safety limit when the caller asks for no limit
    private const int MaxDepth = 64;

    //MaxText caps one expression in the report
    private const int MaxText = 160;

    //MaxDispatch is how many implementations a call site may fan out to before it falls back to the declaration
    private const int MaxDispatch = 8;

    public static IReadOnlyList<TaintPath> Analyze(Compilation compilation, IReadOnlyCollection<string> sources,
        TaintOptions options, out string error)
    {
        error = string.Empty;
        var methods = Methods(compilation, sources, options);
        if (methods.Count == 0)
        {
            error = "no method matched the selected files, classes or methods";
            return [];
        }

        var walker = new Walker(compilation, new TaintMatcher(options.Rules), options);
        foreach (var method in methods)
        {
            walker.Run(method);
            if (walker.Paths.Count >= options.MaxPaths)
                break;
        }

        return walker.Paths;
    }

    //CheckMethod runs the same engine for one method, with every parameter treated as untrusted
    //The hook check uses this to see whether a replacement hands its arguments to a dangerous api
    public static IReadOnlyList<TaintPath> CheckMethod(Compilation compilation, IMethodSymbol method,
        TaintOptions options)
    {
        var walker = new Walker(compilation, new TaintMatcher(options.Rules), options);
        walker.Run(method);
        return walker.Paths;
    }

    //Methods every method declared in the selected sources that passes the filters
    private static List<IMethodSymbol> Methods(Compilation compilation, IReadOnlyCollection<string> sources,
        TaintOptions options)
    {
        var found = new List<IMethodSymbol>();
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!sources.Contains(tree.FilePath, StringComparer.OrdinalIgnoreCase))
                continue;

            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes()
                         .Where(node => node is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax))
            {
                if (model.GetDeclaredSymbol(node) is not IMethodSymbol method || method.IsImplicitlyDeclared)
                    continue;
                if (method.MethodKind == MethodKind.LocalFunction || !Matches(method, options))
                    continue;
                if (seen.Add(method))
                    found.Add(method);
            }
        }

        return found
            .OrderBy(PathOf, StringComparer.Ordinal)
            .ThenBy(StartOf)
            .ToList();
    }

    private static bool Matches(IMethodSymbol method, TaintOptions options)
    {
        if (options.Roots.Count > 0 && !options.Roots.Any(name => MatchesName(method, name)))
            return false;

        if (options.Classes.Count > 0
            && (method.ContainingType is null
                || !options.Classes.Any(name => MatchesType(method.ContainingType, name))))
            return false;

        return options.Methods.Count == 0 || options.Methods.Any(name => MatchesName(method, name));
    }

    private static bool MatchesName(IMethodSymbol method, string name)
    {
        var type = method.ContainingType;
        var wanted = name.Replace('+', '.').Replace('/', '.');
        return method.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || (type is not null && $"{type.Name}.{method.Name}".Equals(name, StringComparison.OrdinalIgnoreCase))
            || (type is not null && $"{type.ToDisplayString()}.{method.Name}"
                .Replace('+', '.').Replace('/', '.').Equals(wanted, StringComparison.OrdinalIgnoreCase))
            || method.ToDisplayString().Replace('+', '.').Replace('/', '.')
                .Equals(wanted, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesType(INamedTypeSymbol type, string name)
    {
        var wanted = name.Replace('+', '.').Replace('/', '.');
        return type.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || type.ToDisplayString().Replace('+', '.').Replace('/', '.')
                .Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || type.MetadataName.Replace('+', '.').Equals(wanted, StringComparison.OrdinalIgnoreCase);
    }

    private static string PathOf(IMethodSymbol method)
        => method.Locations.FirstOrDefault(location => location.IsInSource)?.SourceTree?.FilePath ?? string.Empty;

    private static int StartOf(IMethodSymbol method)
        => method.Locations.FirstOrDefault(location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue;

    //BodyOf the operation tree behind a declaration, null for an abstract or interface method
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

    private static ISymbol? SymbolOf(IOperation operation) => operation switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        _ => null,
    };

    private static string Text(SyntaxNode node)
    {
        var text = node.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        return text.Length <= MaxText ? text : text[..MaxText] + "...";
    }

    //Value what one expression carries: the origins that reach it and the heap slots it may point at
    private sealed class Value
    {
        public HashSet<int> Origins { get; } = new();

        public HashSet<int> Slots { get; } = new();

        public Value Copy()
        {
            var copy = new Value();
            copy.Origins.UnionWith(Origins);
            copy.Slots.UnionWith(Slots);
            return copy;
        }

        public static void Merge(Value into, Value other)
        {
            into.Origins.UnionWith(other.Origins);
            into.Slots.UnionWith(other.Slots);
        }
    }

    private sealed class Walker
    {
        private readonly Compilation _compilation;
        private readonly TaintMatcher _matcher;
        private readonly TaintOptions _options;
        private readonly int _depth;

        //_static holds the static fields the walk has written, which belong to no instance
        private readonly Dictionary<ISymbol, Value> _static = new(SymbolEqualityComparer.Default);

        //_heap holds one member table per allocated object, keyed by a slot that stays stable across calls
        private readonly Dictionary<int, Dictionary<ISymbol, Value>> _heap = new();

        //_dirty marks a slot whose constructor took untrusted arguments, since that state is not observable
        private readonly HashSet<int> _dirty = new();

        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        //_thisSlots the objects the current receiver may be, swapped per call so a field write lands on it
        private HashSet<int> _thisSlots = new();

        private int _nextSlot = 1;

        //_dispatch caches the implementations a call site may run, so a wide contract is resolved once
        private readonly Dictionary<IMethodSymbol, IReadOnlyList<IMethodSymbol>> _dispatch =
            new(SymbolEqualityComparer.Default);

        //_types the source types, where an implementation of a contract can live
        private List<INamedTypeSymbol>? _types;

        //_slotTypes the declared type of each allocated object, which narrows a polymorphic call to one implementation
        private readonly Dictionary<int, INamedTypeSymbol> _slotTypes = new();

        //_overrides and _implementations map a contract to what can run instead, built once per walk
        private Dictionary<IMethodSymbol, List<IMethodSymbol>>? _overrides;
        private Dictionary<IMethodSymbol, List<IMethodSymbol>>? _implementations;

        //Writers store their argument into the receiver, which is how a collection or builder carries it afterwards
        private static readonly HashSet<string> Writers = new(StringComparer.Ordinal)
        {
            "Add", "AddRange", "AddFirst", "AddLast", "AddOrUpdate", "GetOrAdd",
            "Insert", "InsertRange", "TryAdd",
            "Enqueue", "Push", "Append", "AppendLine", "AppendFormat",
            "Write", "WriteLine", "WriteByte", "Set", "SetValue",
        };

        public Walker(Compilation compilation, TaintMatcher matcher, TaintOptions options)
        {
            _compilation = compilation;
            _matcher = matcher;
            _options = options;
            _depth = options.Depth > 0 ? Math.Min(options.Depth, MaxDepth) : MaxDepth;
        }

        public List<TaintPath> Paths { get; } = new();

        //Run treats every parameter of the root as untrusted, unless the call names the ones to seed
        public void Run(IMethodSymbol method)
        {
            var seed = new Dictionary<ISymbol, Value>(SymbolEqualityComparer.Default);
            for (var index = 0; index < method.Parameters.Length; index++)
            {
                if (!Selected(method.Parameters[index]))
                    continue;

                var value = new Value();
                value.Origins.Add(index);
                seed[method.Parameters[index]] = value;
            }

            _thisSlots = new HashSet<int>();
            if (!method.IsStatic && method.ContainingType is not null)
                _thisSlots.Add(Allocate());

            WalkMethod(method, seed, 0, new List<IMethodSymbol> { method });
        }

        //WalkMethod returns what the method hands back, null when there is no body to read
        private Value? WalkMethod(IMethodSymbol method, Dictionary<ISymbol, Value> seed, int depth,
            List<IMethodSymbol> stack)
        {
            if (depth > _depth || Paths.Count >= _options.MaxPaths)
                return null;

            var body = BodyOf(_compilation, method);
            if (body is null)
                return null;

            var taint = new Dictionary<ISymbol, Value>(seed, SymbolEqualityComparer.Default);
            var returns = new Value();
            var previous = _thisSlots;
            if (!method.IsStatic && method.ContainingType is not null
                && seed.TryGetValue(method.ContainingType, out var self))
                _thisSlots = self.Slots;

            Walk(body, taint, returns, depth, stack);
            _thisSlots = previous;
            return returns;
        }

        private void Walk(IOperation operation, Dictionary<ISymbol, Value> taint, Value returns,
            int depth, List<IMethodSymbol> stack)
        {
            switch (operation)
            {
                case IBlockOperation block:
                    foreach (var child in block.Operations)
                        Walk(child, taint, returns, depth, stack);
                    break;
                case IExpressionStatementOperation statement:
                    Taint(statement.Operation, taint, depth, stack);
                    break;
                case IVariableDeclarationGroupOperation group:
                    foreach (var declaration in group.Declarations)
                    {
                        foreach (var declarator in declaration.Declarators)
                        {
                            var value = Taint(declarator.Initializer?.Value, taint, depth, stack);
                            if (Holds(value))
                                taint[declarator.Symbol] = value.Copy();
                            else
                                taint.Remove(declarator.Symbol);
                        }
                    }
                    break;
                case IConditionalOperation conditional:
                    Taint(conditional.Condition, taint, depth, stack);
                    Walk(conditional.WhenTrue, taint, returns, depth, stack);
                    if (conditional.WhenFalse is not null)
                        Walk(conditional.WhenFalse, taint, returns, depth, stack);
                    break;
                case ILoopOperation loop:
                    if (loop is IForLoopOperation @for)
                    {
                        foreach (var before in @for.Before)
                            Walk(before, taint, returns, depth, stack);
                        Taint(@for.Condition, taint, depth, stack);
                    }
                    else if (loop is IForEachLoopOperation @foreach)
                    {
                        Taint(@foreach.Collection, taint, depth, stack);
                    }
                    else if (loop is IWhileLoopOperation @while)
                    {
                        Taint(@while.Condition, taint, depth, stack);
                    }

                    Walk(loop.Body, taint, returns, depth, stack);
                    break;
                case ISwitchOperation @switch:
                    Taint(@switch.Value, taint, depth, stack);
                    foreach (var item in @switch.Cases)
                    {
                        foreach (var child in item.Body)
                            Walk(child, taint, returns, depth, stack);
                    }
                    break;
                case ITryOperation @try:
                    Walk(@try.Body, taint, returns, depth, stack);
                    foreach (var handler in @try.Catches)
                        Walk(handler.Handler, taint, returns, depth, stack);
                    if (@try.Finally is not null)
                        Walk(@try.Finally, taint, returns, depth, stack);
                    break;
                case IUsingOperation @using:
                    Taint(@using.Resources, taint, depth, stack);
                    Walk(@using.Body, taint, returns, depth, stack);
                    break;
                case IUsingDeclarationOperation usingDeclaration:
                    Taint(usingDeclaration.DeclarationGroup, taint, depth, stack);
                    break;
                case ILockOperation @lock:
                    Taint(@lock.LockedValue, taint, depth, stack);
                    Walk(@lock.Body, taint, returns, depth, stack);
                    break;
                case IReturnOperation @return:
                    Value.Merge(returns, Taint(@return.ReturnedValue, taint, depth, stack));
                    break;
                case IThrowOperation @throw:
                    Taint(@throw.Exception, taint, depth, stack);
                    break;
                case ILocalFunctionOperation:
                    break;
                default:
                    Taint(operation, taint, depth, stack);
                    break;
            }
        }

        private Value Taint(IOperation? operation, Dictionary<ISymbol, Value> taint, int depth,
            List<IMethodSymbol> stack)
        {
            if (operation is null)
                return new Value();

            switch (operation)
            {
                case ILocalReferenceOperation local:
                    return Lookup(taint, local.Local);
                case IParameterReferenceOperation parameter:
                    return Lookup(taint, parameter.Parameter);
                case IFieldReferenceOperation field:
                    return LookupField(field.Field, field.Instance, taint, depth, stack);
                case IPropertyReferenceOperation property:
                    return LookupField(property.Property, property.Instance, taint, depth, stack);
                case ISimpleAssignmentOperation assignment:
                    return Assign(assignment.Target, assignment.Value, taint, depth, stack);
                case ICompoundAssignmentOperation compound:
                    return Assign(compound.Target, compound.Value, taint, depth, stack, compound.Target);
                case IIncrementOrDecrementOperation increment:
                    {
                        var value = Taint(increment.Target, taint, depth, stack);
                        Write(increment.Target, value, taint);
                        return value;
                    }
                case IVariableDeclaratorOperation declarator:
                    {
                        var value = Taint(declarator.Initializer?.Value, taint, depth, stack);
                        if (Holds(value))
                            taint[declarator.Symbol] = value.Copy();
                        else
                            taint.Remove(declarator.Symbol);
                        return value;
                    }
                case IAwaitOperation @await:
                    return Taint(@await.Operation, taint, depth, stack);
                case IConversionOperation conversion:
                    return Taint(conversion.Operand, taint, depth, stack);
                case IConditionalAccessOperation access:
                    {
                        var value = Taint(access.Operation, taint, depth, stack);
                        Value.Merge(value, Taint(access.WhenNotNull, taint, depth, stack));
                        return value;
                    }
                case IBinaryOperation binary:
                    {
                        var value = Taint(binary.LeftOperand, taint, depth, stack);
                        Value.Merge(value, Taint(binary.RightOperand, taint, depth, stack));
                        return value;
                    }
                case IInterpolatedStringOperation interpolated:
                    {
                        var value = new Value();
                        foreach (var part in interpolated.Parts)
                            Value.Merge(value, Taint(part, taint, depth, stack));
                        return value;
                    }
                case IArrayElementReferenceOperation element:
                    return Taint(element.ArrayReference, taint, depth, stack);
                case IObjectCreationOperation creation:
                    return Creation(creation, taint, depth, stack);
                case IInvocationOperation invocation:
                    return Invocation(invocation, taint, depth, stack);
                default:
                    {
                        var value = new Value();
                        foreach (var child in operation.ChildOperations)
                            Value.Merge(value, Taint(child, taint, depth, stack));
                        return value;
                    }
            }
        }

        private Value LookupField(ISymbol symbol, IOperation? instance, Dictionary<ISymbol, Value> taint, int depth,
            List<IMethodSymbol> stack)
        {
            var value = new Value();
            if (instance is not null && instance is not IInstanceReferenceOperation)
                Value.Merge(value, Taint(instance, taint, depth, stack));

            if (!_options.Fields)
                return value;

            //The slots are copied because merging what a field holds adds slots to the same value being read
            var slots = instance is null or IInstanceReferenceOperation
                ? new HashSet<int>(_thisSlots)
                : new HashSet<int>(value.Slots);
            if (IsStatic(symbol))
            {
                if (_static.TryGetValue(symbol, out var stored))
                    Value.Merge(value, stored);
                return value;
            }

            foreach (var slot in slots)
            {
                if (_heap.TryGetValue(slot, out var members) && members.TryGetValue(symbol, out var stored))
                    Value.Merge(value, stored);
                if (_dirty.Contains(slot))
                    value.Origins.Add(-1);
            }

            return value;
        }

        private Value Assign(IOperation target, IOperation? source, Dictionary<ISymbol, Value> taint, int depth,
            List<IMethodSymbol> stack, IOperation? extra = null)
        {
            var value = Taint(source, taint, depth, stack);
            if (extra is not null)
                Value.Merge(value, Taint(extra, taint, depth, stack));

            Write(target, value, taint);
            return value;
        }

        //Write stores a value on the symbol it names, on the static table or on the object the target belongs to
        private void Write(IOperation target, Value value, Dictionary<ISymbol, Value> taint)
        {
            var symbol = SymbolOf(target);
            if (symbol is null)
                return;

            var copy = value.Copy();
            if (!_options.Fields || symbol is not IFieldSymbol and not IPropertySymbol)
            {
                if (Holds(copy))
                    taint[symbol] = copy;
                else
                    taint.Remove(symbol);
                return;
            }

            if (IsStatic(symbol))
            {
                if (Holds(copy))
                    _static[symbol] = copy;
                else
                    _static.Remove(symbol);
                return;
            }

            var instance = target switch
            {
                IFieldReferenceOperation field => field.Instance,
                IPropertyReferenceOperation property => property.Instance,
                _ => null,
            };

            if (!Holds(copy))
            {
                foreach (var slot in PointerOf(instance, taint))
                    if (_heap.TryGetValue(slot, out var members))
                        members.Remove(symbol);
                return;
            }

            foreach (var slot in PointerOf(instance, taint))
            {
                if (!_heap.TryGetValue(slot, out var members))
                    _heap[slot] = members = new Dictionary<ISymbol, Value>(SymbolEqualityComparer.Default);
                members[symbol] = copy;
            }
        }

        private Value Creation(IObjectCreationOperation creation, Dictionary<ISymbol, Value> taint, int depth,
            List<IMethodSymbol> stack)
        {
            var slot = Allocate();
            var value = new Value();
            value.Slots.Add(slot);
            if (creation.Type is INamedTypeSymbol created)
                _slotTypes[slot] = created;

            foreach (var argument in creation.Arguments)
                Value.Merge(value, Taint(argument.Value, taint, depth, stack));

            if (value.Origins.Count > 0)
                _dirty.Add(slot);

            if (creation.Initializer is not null)
            {
                var previous = _thisSlots;
                _thisSlots = new HashSet<int> { slot };
                Taint(creation.Initializer, taint, depth, stack);
                _thisSlots = previous;
            }

            if (IsSink(creation.Constructor) && Carries(value))
                RecordSink(creation, stack);
            return value;
        }

        private Value Invocation(IInvocationOperation invocation, Dictionary<ISymbol, Value> taint, int depth,
            List<IMethodSymbol> stack)
        {
            var value = new Value();
            var arguments = new Value();
            foreach (var argument in invocation.Arguments)
            {
                var argumentValue = Taint(argument.Value, taint, depth, stack);
                Value.Merge(arguments, argumentValue);
                Value.Merge(value, argumentValue);
            }

            if (invocation.Instance is not null)
                Value.Merge(value, Taint(invocation.Instance, taint, depth, stack));

            var target = invocation.TargetMethod;

            //A write method marks its receiver, which is how a collection or builder carries what was added to it
            if (invocation.Instance is not null && Holds(arguments) && Writers.Contains(target.Name))
                foreach (var slot in PointerOf(invocation.Instance, taint))
                    _dirty.Add(slot);

            var kind = _matcher.Match(target);
            if (kind == TaintKind.Sink || IsSink(target))
            {
                if (Carries(value))
                    RecordSink(invocation, stack);
                return new Value();
            }

            if (kind == TaintKind.Sanitizer)
                return new Value();

            if (kind == TaintKind.Source)
                return Source();

            if (_options.Interprocedural && depth < _depth)
            {
                var receivers = invocation.Instance is null
                    ? new HashSet<int>()
                    : PointerOf(invocation.Instance, taint);
                var handles = Dispatch(target, receivers);
                var returned = new Value();
                var walked = 0;
                foreach (var handle in handles)
                {
                    if (!CallGraph.Local(handle) || stack.Any(item => Same(item, handle)))
                        continue;

                    var seed = new Dictionary<ISymbol, Value>(SymbolEqualityComparer.Default);
                    for (var index = 0;
                         index < handle.Parameters.Length && index < invocation.Arguments.Length;
                         index++)
                    {
                        var argument = Taint(invocation.Arguments[index].Value, taint, depth, stack);
                        if (Holds(argument))
                            seed[handle.Parameters[index]] = argument;
                    }

                    if (!handle.IsStatic && handle.ContainingType is not null && invocation.Instance is not null)
                    {
                        var receiver = Taint(invocation.Instance, taint, depth, stack);
                        var self = new Value();
                        self.Slots.UnionWith(receiver.Slots);
                        seed[handle.ContainingType] = self;
                    }

                    if (seed.Count == 0)
                        continue;

                    var next = new List<IMethodSymbol>(stack) { handle };
                    var result = WalkMethod(handle, seed, depth + 1, next);
                    if (result is null)
                        continue;

                    Value.Merge(returned, result);
                    walked++;
                }

                //Only a body that was actually read decides the result; a call with no source keeps what it was given
                if (walked > 0)
                    return returned;
            }

            //A call outside the sources keeps what it was given only when external propagation is allowed
            if (!CallGraph.Local(target) && !_options.External)
                return new Value();

            return value;
        }

        //Dispatch the methods a call may run here, narrowed by the receiver type when the walk knows it
        private IReadOnlyList<IMethodSymbol> Dispatch(IMethodSymbol method, HashSet<int> receivers)
        {
            var handles = AllHandles(method);
            if (receivers.Count == 0 || handles.Count <= 1)
                return handles;

            var narrowed = handles
                .Where(handle => receivers.Any(slot => Accepts(TypeOf(slot), handle)))
                .ToList();
            return narrowed.Count > 0 ? narrowed : handles;
        }

        //AllHandles every implementation of a contract, capped so a wide interface does not explode the walk
        private IReadOnlyList<IMethodSymbol> AllHandles(IMethodSymbol method)
        {
            if (_dispatch.TryGetValue(method, out var cached))
                return cached;

            IReadOnlyList<IMethodSymbol> result = [method];
            if (Polymorphic(method))
            {
                Index();
                var map = (method.ContainingType is { TypeKind: TypeKind.Interface }
                    ? _implementations
                    : _overrides)!;
                if (map.TryGetValue(method, out var found) && found.Count is > 0 and <= MaxDispatch)
                    result = found;
            }

            _dispatch[method] = result;
            return result;
        }

        //Index builds both dispatch maps in one pass, so a call site does not scan every type again
        private void Index()
        {
            if (_overrides is not null)
                return;

            _overrides = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);
            _implementations = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(SymbolEqualityComparer.Default);

            foreach (var type in Types())
            {
                foreach (var member in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (member.OverriddenMethod is null)
                        continue;

                    var root = member.OverriddenMethod;
                    while (root.OverriddenMethod is not null)
                        root = root.OverriddenMethod;
                    Add(_overrides, root, member);
                }

                if (type.TypeKind == TypeKind.Interface)
                    continue;

                foreach (var contract in type.AllInterfaces)
                {
                    foreach (var member in contract.GetMembers().OfType<IMethodSymbol>())
                    {
                        if (type.FindImplementationForInterfaceMember(member) is not IMethodSymbol implementation)
                            continue;
                        Add(_implementations, member, implementation);
                    }
                }
            }
        }

        private static void Add(Dictionary<IMethodSymbol, List<IMethodSymbol>> map, IMethodSymbol key,
            IMethodSymbol value)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<IMethodSymbol>();
            if (!list.Contains(value, SymbolEqualityComparer.Default))
                list.Add(value);
        }

        private INamedTypeSymbol? TypeOf(int slot)
            => _slotTypes.TryGetValue(slot, out var type) ? type : null;

        //Accepts whether an object of the given type can run the implementation
        private static bool Accepts(INamedTypeSymbol? type, IMethodSymbol handle)
        {
            var owner = handle.ContainingType;
            if (type is null || owner is null)
                return true;
            if (Same(type, owner))
                return true;

            for (var current = type.BaseType; current is not null; current = current.BaseType)
                if (Same(current, owner))
                    return true;

            return type.AllInterfaces.Any(item => Same(item, owner));
        }

        private static bool Polymorphic(IMethodSymbol method)
            => method.ContainingType is { TypeKind: not TypeKind.Error, SpecialType: not SpecialType.System_Object } owner
                && (owner.TypeKind == TypeKind.Interface
                    || (method.IsAbstract || method.IsVirtual) && !method.IsOverride);

        //Types every type declared in the sources, which is where an implementation of a contract can live
        private List<INamedTypeSymbol> Types()
        {
            if (_types is not null)
                return _types;

            _types = _compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .Where(type => type.Locations.Any(location => location.IsInSource))
                .ToList();
            return _types;
        }

        private static bool Same(IMethodSymbol left, IMethodSymbol right)
            => SymbolEqualityComparer.Default.Equals(left, right)
                || SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);

        private static bool Same(INamedTypeSymbol left, INamedTypeSymbol right)
            => SymbolEqualityComparer.Default.Equals(left, right)
                || SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition);

        //Carries whether a value still holds something untrusted, either itself or through an object it names
        private bool Carries(Value value)
        {
            if (value.Origins.Count > 0)
                return true;

            foreach (var slot in value.Slots)
            {
                if (_dirty.Contains(slot))
                    return true;
                if (_heap.TryGetValue(slot, out var members)
                    && members.Values.Any(item => item.Origins.Count > 0))
                    return true;
            }

            return false;
        }

        //Holds whether a value is worth keeping, which includes a clean object because its fields may change later
        private static bool Holds(Value value) => value.Origins.Count > 0 || value.Slots.Count > 0;

        private HashSet<int> PointerOf(IOperation? instance, Dictionary<ISymbol, Value> taint)
        {
            if (instance is null or IInstanceReferenceOperation)
                return _thisSlots;

            var symbol = SymbolOf(instance);
            return symbol is not null && taint.TryGetValue(symbol, out var value)
                ? value.Slots
                : new HashSet<int>();
        }

        private bool IsSink(IMethodSymbol? method)
            => _options.MemorySinks
                && method is not null
                && method.ContainingType is { TypeKind: not TypeKind.Error }
                && MemorySafetyCatalog.Kind(method) is not null;

        private void RecordSink(IOperation operation, List<IMethodSymbol> stack)
        {
            if (Paths.Count >= _options.MaxPaths || stack.Count == 0)
                return;

            var target = operation switch
            {
                IInvocationOperation invocation => invocation.TargetMethod,
                IObjectCreationOperation creation => creation.Constructor,
                _ => null,
            };
            if (target is null)
                return;

            var chain = string.Join(" -> ", stack.Select(CallGraph.Name)) + " -> " + CallGraph.Name(target);
            var location = operation.Syntax.GetLocation();
            var span = location.GetLineSpan();
            var key = $"{chain}|{span.Path}:{span.StartLinePosition.Line}:{span.StartLinePosition.Character}";
            if (!_seen.Add(key))
                return;

            Paths.Add(new TaintPath(CallGraph.Name(stack[0]), chain, location, Text(operation.Syntax)));
        }

        private int Allocate() => _nextSlot++;

        //Selected whether a root parameter is seeded, which is all of them unless the call names some
        private bool Selected(IParameterSymbol parameter)
        {
            var names = _options.RootParameters;
            return names.Count == 0
                || names.Any(name => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsStatic(ISymbol symbol)
            => symbol is IFieldSymbol { IsStatic: true } or IPropertySymbol { IsStatic: true };

        private static Value Source()
        {
            var value = new Value();
            value.Origins.Add(-1);
            return value;
        }

        private static Value Lookup(Dictionary<ISymbol, Value> taint, ISymbol symbol)
            => taint.TryGetValue(symbol, out var value) ? value.Copy() : new Value();
    }
}
