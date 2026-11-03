namespace NacreCraft.ModBuild.Tools.Experimental.Java;

//JavaCallGraph holds the methods of a Java source tree and the calls between them
//It mirrors the shape of the C# graph the shipped callgraph prints, without the cycle check that one runs
internal sealed class JavaCallGraph
{
    //Method one method or constructor; Key is what edges resolve against and Local says the sources declare it
    internal sealed class Method
    {
        public required string Key { get; init; }
        public required string Type { get; init; }
        public required string SimpleType { get; init; }
        public required string Name { get; init; }
        public required int Arity { get; init; }
        public string File { get; init; } = string.Empty;
        public int Line { get; init; }
        public bool Local { get; init; }
        public bool IsPrivate { get; init; }
        public bool IsStatic { get; init; }

        public List<Edge> Outgoing { get; } = new();

        public int Incoming { get; set; }

        //Signature is what the report prints; a constructor reads as Type.<init>
        public string Signature => $"{SimpleType}.{Name}";
    }

    internal readonly record struct Edge(Method Target, int Line);

    private readonly Dictionary<string, Method> _methods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Method> _externals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Method>> _byNameArity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _typesBySimple = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string File, int Line)> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _display = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _supers = new(StringComparer.Ordinal);

    //calls whose receiver type could not be worked out and that matched no single method
    public int Unresolved { get; set; }

    public IEnumerable<Method> Local => _methods.Values;

    public int CallCount => _methods.Values.Sum(method => method.Outgoing.Count);

    public int ExternalCount => _methods.Values.Sum(method => method.Outgoing.Count(edge => !edge.Target.Local));

    public void AddType(string fullName, string display, string file, int line)
    {
        _types.TryAdd(fullName, (file, line));
        _display.TryAdd(fullName, display);

        var dot = fullName.LastIndexOf('.');
        var simple = dot < 0 ? fullName : fullName[(dot + 1)..];
        if (!_typesBySimple.TryGetValue(simple, out var names))
            _typesBySimple[simple] = names = new List<string>();
        if (!names.Contains(fullName, StringComparer.Ordinal))
            names.Add(fullName);
    }

    public void SetSuper(string type, string super)
    {
        if (super.Length > 0)
            _supers[type] = super;
    }

    public string? SuperOf(string type)
        => _supers.TryGetValue(type, out var super) ? super : null;

    //TypeOf resolves a name written in code to a declared type: an exact full name wins, then a unique simple name
    public string? TypeOf(string name)
    {
        if (_types.ContainsKey(name))
            return name;

        if (!_typesBySimple.TryGetValue(name, out var candidates))
        {
            //a qualified name that was not imported still names a type by its last segment
            var dot = name.LastIndexOf('.');
            if (dot < 0 || !_typesBySimple.TryGetValue(name[(dot + 1)..], out candidates))
                return null;
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    public Method AddMethod(string type, string name, int arity, string file, int line, bool isPrivate, bool isStatic)
    {
        var key = $"{type}#{name}/{arity}";
        if (_methods.TryGetValue(key, out var existing))
            return existing;

        var method = new Method
        {
            Key = key,
            Type = type,
            SimpleType = _display.GetValueOrDefault(type, SimpleName(type)),
            Name = name,
            Arity = arity,
            File = file,
            Line = line,
            Local = true,
            IsPrivate = isPrivate,
            IsStatic = isStatic,
        };

        _methods[key] = method;
        if (!_byNameArity.TryGetValue($"{name}/{arity}", out var list))
            _byNameArity[$"{name}/{arity}"] = list = new List<Method>();
        list.Add(method);
        return method;
    }

    //LocalConstructor returns the constructor declared for a type, or a stand-in for the implicit one every class has
    public Method? Constructor(string type, int arity)
    {
        var key = $"{type}#<init>/{arity}";
        if (_methods.TryGetValue(key, out var declared))
            return declared;

        if (arity != 0 || !_types.TryGetValue(type, out var location))
            return null;

        return AddMethod(type, "<init>", 0, location.File, location.Line, isPrivate: false, isStatic: false);
    }

    public Method? Find(string type, string name, int arity)
        => _methods.TryGetValue($"{type}#{name}/{arity}", out var method) ? method : null;

    public Method? FindAnyArity(string type, string name)
        => _methods.Values.FirstOrDefault(method => method.Type == type && method.Name == name);

    //Candidates lists declared methods with this name and argument count, used when the receiver type is unknown
    public IReadOnlyList<Method> Candidates(string name, int arity)
        => _byNameArity.TryGetValue($"{name}/{arity}", out var list) ? list : Array.Empty<Method>();

    //External stands in for a method outside the sources, one node per receiver, name and argument count
    public Method External(string type, string name, int arity)
    {
        var key = $"!{type}#{name}/{arity}";
        if (_externals.TryGetValue(key, out var existing))
            return existing;

        var method = new Method
        {
            Key = key,
            Type = type,
            //an outside receiver is shown as it was written, since that is all the sources know about it
            SimpleType = type,
            Name = name,
            Arity = arity,
        };

        _externals[key] = method;
        return method;
    }

    public void Connect(Method caller, Method target, int line)
    {
        caller.Outgoing.Add(new Edge(target, line));
        target.Incoming++;
    }

    private static string SimpleName(string type)
    {
        var dot = type.LastIndexOf('.');
        return dot < 0 ? type : type[(dot + 1)..];
    }
}
