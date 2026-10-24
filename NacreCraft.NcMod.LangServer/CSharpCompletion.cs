using System.Text;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using NacreCraft.ModBuild.Compile;
using NacreCraft.ModBuild.Core;

namespace NacreCraft.NcMod.LangServer;

//CSharpCompletion answers completion from the same compilation the module check builds
//Only the document tree is replaced, so an unsaved buffer completes against the real project references
internal sealed class CSharpCompletion
{
    private readonly ProjectRegistry _projects;

    //One compilation per project root, reused until a save or a watched file invalidates it
    private readonly Dictionary<string, ProjectCompilation> _compilations = new(StringComparer.OrdinalIgnoreCase);

    public CSharpCompletion(ProjectRegistry projects) => _projects = projects;

    //Clear drops the cached compilation of one project, or all of them when no root is given
    public void Clear(string? root = null)
    {
        if (root is null)
            _compilations.Clear();
        else
            _compilations.Remove(root);
    }

    //Complete lists the symbols reachable at a zero based position of one C# document
    //Building the project compilation is only done when allowBuild is on, since that is a full build
    public JsonObject? Complete(string path, string text, int line, int character, bool allowBuild)
    {
        //a new file may live in a folder that is not on disk yet, so the search starts at the nearest real one
        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory);

        if (string.IsNullOrEmpty(directory))
            return null;

        NcProject? config;
        try
        {
            config = NcProject.TryFind(directory, out _);
        }
        catch (IOException)
        {
            return null;
        }

        if (config is null)
            return null;

        var root = Path.GetDirectoryName(config.Path);
        if (root is null)
            return null;

        //the project has to be opened before its documents get semantic help
        if (!_projects.IsLoaded(root))
            return null;

        try
        {
            if (!_compilations.TryGetValue(root, out var project))
            {
                //a completion request never starts a full build on its own; the user has to allow it
                if (!allowBuild)
                    return null;

                var built = CompilationFactory.Create(root, CompileOptions.From(config, config.AssemblyNameOf(root)), out _);
                if (built is null)
                    return null;

                _compilations[root] = built;
                project = built;
            }

            var tree = project.Compilation.SyntaxTrees.FirstOrDefault(item =>
                string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase));
            var parseOptions = tree?.Options as CSharpParseOptions ?? project.ParseOptions;
            var buffer = CSharpSyntaxTree.ParseText(text, parseOptions, path: path, encoding: Encoding.UTF8);

            var compilation = tree is null
                ? project.Compilation.AddSyntaxTrees(buffer)
                : project.Compilation.ReplaceSyntaxTree(tree, buffer);

            var model = compilation.GetSemanticModel(buffer);
            var source = buffer.GetText();
            var lines = source.Lines;
            var row = Math.Clamp(line, 0, Math.Max(lines.Count - 1, 0));
            var column = Math.Clamp(character, 0, lines[row].Span.Length);
            var position = lines[row].Start + column;

            var token = buffer.GetRoot().FindToken(Math.Max(position - 1, 0));
            var prefix = token.IsKind(SyntaxKind.IdentifierToken)
                         && position >= token.SpanStart
                         && position <= token.Span.End
                ? token.Text[..(position - token.SpanStart)]
                : string.Empty;

            var access = token.Parent?.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
            var entries = access is not null
                ? Members(compilation, model, access, position, prefix)
                : Locals(model, position, prefix);

            var items = new JsonArray();
            foreach (var entry in entries)
            {
                items.Add(new JsonObject
                {
                    ["label"] = entry.Name,
                    ["kind"] = entry.Kind,
                    ["detail"] = entry.Detail,
                });
            }

            return new JsonObject
            {
                ["isIncomplete"] = false,
                ["items"] = items,
            };
        }
        catch
        {
            //a project that cannot compile must not take the editor session down; the next save tries again
            _compilations.Remove(root);
            return null;
        }
    }

    //Members lists what can follow a dot, filtered by static access and by what this position can see
    private static List<Entry> Members(Compilation compilation, SemanticModel model,
        MemberAccessExpressionSyntax access, int position, string prefix)
    {
        var type = model.GetTypeInfo(access.Expression).Type;
        if (type is null)
            return [];

        var staticAccess = model.GetSymbolInfo(access.Expression).Symbol is ITypeSymbol;
        var within = Within(model, position, compilation);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<Entry>();

        foreach (var member in AllMembers(type))
        {
            if (member.IsImplicitlyDeclared || member.IsStatic != staticAccess)
                continue;

            //property and event accessors are not names the user can type, and an indexer has no name
            if (member is IMethodSymbol { MethodKind: not MethodKind.Ordinary })
                continue;

            if (member is IPropertySymbol { IsIndexer: true })
                continue;

            if (!member.Name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (!compilation.IsSymbolAccessibleWithin(member, within))
                continue;

            if (!seen.Add(member.Name))
                continue;

            entries.Add(new Entry(member.Name, KindOf(member), DetailOf(member)));
        }

        return entries
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Take(200)
            .ToList();
    }

    //Locals lists what is in scope when the cursor is not following a dot
    private static List<Entry> Locals(SemanticModel model, int position, string prefix)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<Entry>();

        foreach (var symbol in model.LookupSymbols(position))
        {
            if (symbol.IsImplicitlyDeclared)
                continue;

            if (symbol is IMethodSymbol { MethodKind: not MethodKind.Ordinary })
                continue;

            if (!symbol.Name.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (!seen.Add(symbol.Name))
                continue;

            entries.Add(new Entry(symbol.Name, KindOf(symbol), DetailOf(symbol)));
        }

        return entries
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Take(200)
            .ToList();
    }

    //AllMembers walks the base chain plus every interface a type implements
    private static IEnumerable<ISymbol> AllMembers(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
                yield return member;
        }

        if (type.TypeKind != TypeKind.Interface)
            yield break;

        foreach (var item in type.AllInterfaces)
        {
            foreach (var member in item.GetMembers())
                yield return member;
        }
    }

    //Within is the type or assembly accessibility is judged against
    private static ISymbol Within(SemanticModel model, int position, Compilation compilation)
    {
        var enclosing = model.GetEnclosingSymbol(position);
        if (enclosing is INamedTypeSymbol named)
            return named;

        return (ISymbol?)enclosing?.ContainingType ?? compilation.Assembly;
    }

    //KindOf maps a symbol to the completion kind numbers of the protocol
    private static int KindOf(ISymbol symbol) => symbol switch
    {
        IMethodSymbol => 2,
        IPropertySymbol => 10,
        IFieldSymbol => 5,
        IEventSymbol => 23,
        ILocalSymbol or IParameterSymbol => 6,
        INamespaceSymbol => 9,
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => 13,
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => 8,
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => 22,
        INamedTypeSymbol => 7,
        _ => 12,
    };

    private static string DetailOf(ISymbol symbol)
        => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private readonly record struct Entry(string Name, int Kind, string Detail);
}
