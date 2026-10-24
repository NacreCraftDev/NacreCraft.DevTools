using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
//Roslyn's Diagnostic collides with the local one in this namespace, so alias it
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace NacreCraft.ModBuild.Diagnostics;

//MemorySafetyAnalyzer warns where a mod takes memory management into its own hands
//The kernel loads every mod into the server process, so a wrong offset, a leaked buffer or a dangling handle does not
//stay inside the mod: the whole server goes down with it
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MemorySafetyAnalyzer : DiagnosticAnalyzer
{
    //Code reported for every hit
    public const string Code = "NC0003";

    //AdviceProperty carries the "use this instead" line, which the renderer shows as the fix under the diagnostic
    public const string AdviceProperty = "advice";

    //Rule the one descriptor every hit is reported through, the message alone says which api and why
    private static readonly DiagnosticDescriptor Rule = new(
        Code,
        "Manual memory operation",
        "{0}",
        "MemorySafety",
        Microsoft.CodeAnalysis.DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    //NativeAttributes the declarations that bind a method to a native library
    private static readonly string[] NativeAttributes =
    [
        "System.Runtime.InteropServices.DllImportAttribute",
        "System.Runtime.InteropServices.LibraryImportAttribute",
    ];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        //generated sources are not the mod's own code
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        //an operation gives the called symbol, so an aliased or statically imported member is recognized the same way
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterSyntaxNodeAction(AnalyzeStackAllocation, SyntaxKind.StackAllocArrayCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeStackAllocation, SyntaxKind.ImplicitStackAllocArrayCreationExpression);
        context.RegisterSymbolAction(AnalyzeDeclaration, SymbolKind.Method);
    }

    //AnalyzeInvocation checks a call against the catalog, which is where every allocation, address and handle shows up
    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;

        //a call that could not be bound is a compile error already, and the missing type would only produce noise here
        if (method.ContainingType is null || method.ContainingType.TypeKind == TypeKind.Error)
            return;

        var kind = MemorySafetyCatalog.Kind(method);
        if (kind is null)
            return;

        context.ReportDiagnostic(Report(invocation.Syntax.GetLocation(), $"{method.ContainingType.Name}.{method.Name}", kind.Value));
    }

    //AnalyzeStackAllocation reports a stack allocation, which only stays safe while the size is small and fixed
    private static void AnalyzeStackAllocation(SyntaxNodeAnalysisContext context)
        => context.ReportDiagnostic(Report(context.Node.GetLocation(), "stackalloc", MemorySafetyKind.Stack));

    //AnalyzeDeclaration reports a method bound to native code, once per declaration rather than once per call site
    private static void AnalyzeDeclaration(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.IsImplicitlyDeclared || !method.Locations.Any(location => location.IsInSource))
            return;

        var binding = method.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() is { } name && NativeAttributes.Contains(name, StringComparer.Ordinal));
        if (binding is null)
            return;

        context.ReportDiagnostic(Report(binding.ApplicationSyntaxReference?.GetSyntax().GetLocation()
            ?? method.Locations.First(), method.Name, MemorySafetyKind.Native));
    }

    //Report builds the diagnostic, attaching the advice so the renderer can show what to use instead
    private static RoslynDiagnostic Report(Location location, string name, MemorySafetyKind kind)
    {
        var (message, advice) = MemorySafetyCatalog.Describe(name, kind);
        return RoslynDiagnostic.Create(
            Rule,
            location,
            ImmutableDictionary<string, string?>.Empty.Add(AdviceProperty, advice),
            message);
    }
}
