using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
//Roslyn's Diagnostic collides with the local one in this namespace, so alias it
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace NacreCraft.ModBuild.Diagnostics;

//CallCycleAnalyzer reports a call cycle that nothing can break
//A stack overflow cannot be caught in .NET, the process dies where it stands, and in the kernel that process is the server
//with every mod loaded into it, so an endless cycle takes the whole game down rather than the one mod
//Only a cycle with no way out is reported: no method on it can leave early, and the recursion never steps away from the
//branch it sits in. That leaves the ordinary recursive helper alone, whether it guards the call with a condition, ends on
//a base case that counts down, or walks a collection
//The graph itself lives in CallGraph so the callgraph command walks the very same edges
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CallCycleAnalyzer : DiagnosticAnalyzer
{
    //Code reported for every cycle found
    public const string Code = "NC0004";

    //Rule the one descriptor every cycle is reported through, the message names the path
    //The end tag marks it as one that only a whole compilation can answer, which is when the graph is walked
    private static readonly DiagnosticDescriptor Rule = new(
        Code,
        "Call cycle without an exit",
        "{0}",
        "MemorySafety",
        Microsoft.CodeAnalysis.DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    //Advice attached to every hit, which the renderer shows as the fix under the diagnostic
    private const string Advice = "break the cycle with a condition or a depth limit in one of the calls";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        //generated sources are not the mod's own code
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        //a cycle spans methods and files, so the edges are gathered when the compilation starts and walked once at the end
        context.RegisterCompilationStartAction(start =>
        {
            var graph = new CallGraph();

            start.RegisterOperationAction(context => Collect(context, graph), OperationKind.Invocation);
            start.RegisterOperationAction(context => Collect(context, graph), OperationKind.ObjectCreation);
            start.RegisterOperationAction(context => MarkExit(context, graph), OperationKind.Return);
            start.RegisterOperationAction(context => MarkExit(context, graph), OperationKind.Throw);
            start.RegisterOperationAction(context => MarkExit(context, graph), OperationKind.Branch);

            start.RegisterCompilationEndAction(context => Report(context, graph));
        });
    }

    //Collect records a call that runs whenever its method runs
    private static void Collect(OperationAnalysisContext context, CallGraph graph)
    {
        var target = CallGraph.Target(context.Operation);
        if (target is null)
            return;

        //A call into a library leaves the project, and whatever comes back comes through the runtime, which no call graph
        //here can follow; only a call between two methods of this project can close a cycle the compiler can see
        if (context.ContainingSymbol is not IMethodSymbol caller || !CallGraph.Local(caller) || !CallGraph.Local(target))
            return;

        //A call from a callback or from a loop is not one that has to happen: a callback runs later, and a loop over a
        //collection ends. A call under a condition is different, since the condition can stay true for every step, so it
        //stays in the graph unless the recursion is seen stepping away from it or the test cannot be read at all
        var shape = CallGraph.Classify(context.Operation);
        if (shape.Deferred || shape.Looped || shape.Step != CallGraph.StepKind.NonTerminating)
            return;

        graph.AddEdge(caller, target, context.Operation.Syntax.GetLocation(), shape.Conditional, cycleEdge: true,
            condition: null);
    }

    //MarkExit notes a method that can leave early, since a cycle through it still ends
    //A return or a throw at the end of the body is the ordinary way out and proves nothing, so only the guarded ones count
    private static void MarkExit(OperationAnalysisContext context, CallGraph graph)
    {
        if (context.ContainingSymbol is not IMethodSymbol method || !CallGraph.Local(method))
            return;

        if (CallGraph.Guarded(context.Operation))
            graph.MarkGuarded(method);
    }

    //Report walks the gathered graph once and reports every cycle that has no way out
    private static void Report(CompilationAnalysisContext context, CallGraph graph)
    {
        foreach (var cycle in graph.Cycles())
        {
            //a cycle whose calls all run outright says one thing, and one that lives inside a condition that never flips
            //says another; both end the same way, but only the second has to explain why the condition does not save it
            var reason = cycle.Conditional
                ? "no method on the cycle can return or throw its way out, and the recursion keeps its own condition true"
                : "no method on the cycle can return or throw its way out, and every call on it runs outright";

            context.ReportDiagnostic(RoslynDiagnostic.Create(
                Rule,
                cycle.Site,
                ImmutableDictionary<string, string?>.Empty.Add(MemorySafetyAnalyzer.AdviceProperty, Advice),
                $"`{cycle.Chain}` has nothing to break it: {reason}, so the stack runs out and takes the process down"));
        }
    }
}
