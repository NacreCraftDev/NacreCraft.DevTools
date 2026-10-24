using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using NacreCraft.ModBuild.Compile;

namespace NacreCraft.ModBuild.Diagnostics;

//Builds a CallGraph from a whole compilation, which is what the callgraph command shows
//The analyzer gathers edges from its own callbacks instead, but both sides share the edge rules in CallGraph
internal static class CallGraphBuilder
{
    public static CallGraph Build(Compilation compilation, IReadOnlyCollection<string> sources,
        Action<int, int, string>? progress = null)
    {
        var paths = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);
        var graph = new CallGraph();
        var trees = compilation.SyntaxTrees.Where(tree => Owned(tree, paths)).ToList();
        var declarations = new Dictionary<SyntaxTree, List<(SyntaxNode Node, IMethodSymbol Method)>>();
        var steps = trees.Count * 2;
        var done = 0;

        //every method declaration becomes a node first, so one that nothing calls still shows up as an entry
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var found = new List<(SyntaxNode Node, IMethodSymbol Method)>();
            foreach (var node in tree.GetRoot().DescendantNodes().Where(IsDeclaration))
            {
                if (model.GetDeclaredSymbol(node) is not IMethodSymbol method || !CallGraph.Local(method))
                    continue;

                graph.Register(method);
                found.Add((node, method));
            }

            declarations[tree] = found;
            progress?.Invoke(++done, steps, tree.FilePath);
        }

        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var (node, method) in declarations[tree])
            {
                var body = model.GetOperation(node);
                if (body is null)
                    continue;

                if (CallGraph.HasExit(body))
                    graph.MarkGuarded(method);

                Collect(body, method, graph);
            }

            progress?.Invoke(++done, steps, tree.FilePath);
        }

        CollectTopLevel(compilation, paths, graph);
        return graph;
    }

    //Owned whether a tree is one of the project's own source files rather than a generated tree
    private static bool Owned(SyntaxTree tree, HashSet<string> paths)
        => tree.FilePath != ProjectCompilation.GeneratedPath && paths.Contains(tree.FilePath);

    private static bool IsDeclaration(SyntaxNode node)
        => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax;

    //Collect adds one edge per call site below a body, with the same flags the analyzer gives them
    private static void Collect(IOperation body, IMethodSymbol caller, CallGraph graph)
    {
        foreach (var operation in CallGraph.Walk(body))
        {
            var target = CallGraph.Target(operation);
            if (target is null)
                continue;

            var shape = CallGraph.Classify(operation);
            graph.AddEdge(caller, target, operation.Syntax.GetLocation(), shape.Conditional,
                !shape.Deferred && !shape.Looped && shape.Step == CallGraph.StepKind.NonTerminating,
                shape.Condition);
        }
    }

    //CollectTopLevel reads the global statements of a program, which compile into a Main method that has no declaration
    //node of its own, so those calls are attributed to the entry point the compiler produced
    private static void CollectTopLevel(Compilation compilation, HashSet<string> paths, CallGraph graph)
    {
        var entry = compilation.GetEntryPoint(default);
        if (entry is null)
            return;

        graph.Register(entry);

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!Owned(tree, paths))
                continue;

            var root = (CompilationUnitSyntax)tree.GetRoot();
            var statements = root.Members.OfType<GlobalStatementSyntax>().ToList();
            if (statements.Count == 0)
                continue;

            var model = compilation.GetSemanticModel(tree);
            foreach (var statement in statements)
            {
                var body = model.GetOperation(statement.Statement);
                if (body is not null)
                    Collect(body, entry, graph);
            }
        }
    }
}
