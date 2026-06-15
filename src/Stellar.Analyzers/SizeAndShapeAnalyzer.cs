using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Stellar.Analyzers;

/// Roslyn discovers analyzer types by scanning the assembly for <see cref="DiagnosticAnalyzerAttribute"/>;
/// this class must be <c>public</c> — a sanctioned exception to the internal-sealed-by-default standard.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SizeAndShapeAnalyzer : DiagnosticAnalyzer
{
    // LoC = physical lines spanned by the method body block, brace line to brace
    // line inclusive (matches docs/coding-standards.md and the qa agent's count).
    private const int MethodMajor = 50;
    private const int MethodBlocker = 100;
    private const int MaxParameters = 5;
    private const int MaxCtorDeps = 6;
    private const int MaxInterfaceMembers = 8;
    private const string AggregatorExemptionName = "IPluginServices"; // see coding-standards.md §I
    private const string AggregatorImplName = "PluginServices"; // aggregator impl; see coding-standards.md §I / IPluginServices exemption

    internal static readonly DiagnosticDescriptor MethodBlockerRule = DiagnosticIds.Make(
        DiagnosticIds.MethodTooLongBlocker, "Method too long (blocker)",
        "Method '{0}' is {1} LoC (> 100); split it");
    internal static readonly DiagnosticDescriptor MethodMajorRule = DiagnosticIds.Make(
        DiagnosticIds.MethodTooLongMajor, "Method too long (major)",
        "Method '{0}' is {1} LoC (> 50); split it");
    internal static readonly DiagnosticDescriptor TooManyParamsRule = DiagnosticIds.Make(
        DiagnosticIds.TooManyParameters, "Too many parameters",
        "Method '{0}' has {1} parameters (> 5); introduce a parameter object");
    internal static readonly DiagnosticDescriptor TooManyCtorDepsRule = DiagnosticIds.Make(
        DiagnosticIds.TooManyCtorDeps, "Too many constructor dependencies",
        "Constructor '{0}' takes {1} dependencies (> 6); the type likely violates SRP");
    internal static readonly DiagnosticDescriptor InterfaceTooWideRule = DiagnosticIds.Make(
        DiagnosticIds.InterfaceTooWide, "Interface too wide",
        "Interface '{0}' declares {1} members (> 8); split by concern");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(MethodBlockerRule, MethodMajorRule, TooManyParamsRule, TooManyCtorDepsRule, InterfaceTooWideRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethodLike,
            SyntaxKind.MethodDeclaration, SyntaxKind.LocalFunctionStatement);
        context.RegisterSyntaxNodeAction(AnalyzeParameters,
            SyntaxKind.MethodDeclaration, SyntaxKind.LocalFunctionStatement);
        context.RegisterSyntaxNodeAction(AnalyzeCtor, SyntaxKind.ConstructorDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeInterface, SyntaxKind.InterfaceDeclaration);
    }

    private static void AnalyzeMethodLike(SyntaxNodeAnalysisContext ctx)
    {
        BlockSyntax? body = ctx.Node switch
        {
            MethodDeclarationSyntax m => m.Body,
            LocalFunctionStatementSyntax f => f.Body,
            _ => null,
        };
        if (body is null) return; // expression-bodied or abstract — not a length risk

        var span = body.GetLocation().GetLineSpan();
        var loc = span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
        if (loc <= MethodMajor) return;

        var name = ctx.Node switch
        {
            MethodDeclarationSyntax m => m.Identifier,
            LocalFunctionStatementSyntax f => f.Identifier,
            _ => default,
        };
        if (loc > MethodBlocker)
            ctx.ReportDiagnostic(Diagnostic.Create(MethodBlockerRule, name.GetLocation(), name.Text, loc));
        ctx.ReportDiagnostic(Diagnostic.Create(MethodMajorRule, name.GetLocation(), name.Text, loc));
    }

    private static void AnalyzeParameters(SyntaxNodeAnalysisContext ctx)
    {
        var (paramList, identifier) = ctx.Node switch
        {
            MethodDeclarationSyntax m => (m.ParameterList, m.Identifier),
            LocalFunctionStatementSyntax f => (f.ParameterList, f.Identifier),
            _ => default,
        };
        if (paramList is null) return;
        var count = paramList.Parameters.Count;
        if (count > MaxParameters)
            ctx.ReportDiagnostic(Diagnostic.Create(TooManyParamsRule, identifier.GetLocation(), identifier.Text, count));
    }

    private static void AnalyzeCtor(SyntaxNodeAnalysisContext ctx)
    {
        var c = (ConstructorDeclarationSyntax)ctx.Node;
        // Documented exception: PluginServices is the aggregator impl (coding-standards.md §I).
        if (c.Parent is TypeDeclarationSyntax t && t.Identifier.Text == AggregatorImplName) return;
        var count = c.ParameterList.Parameters.Count;
        if (count > MaxCtorDeps)
            ctx.ReportDiagnostic(Diagnostic.Create(TooManyCtorDepsRule, c.Identifier.GetLocation(), c.Identifier.Text, count));
    }

    private static void AnalyzeInterface(SyntaxNodeAnalysisContext ctx)
    {
        var i = (InterfaceDeclarationSyntax)ctx.Node;
        // Documented exception: IPluginServices is the aggregator (coding-standards.md §I).
        if (i.Identifier.Text == AggregatorExemptionName) return;
        var count = i.Members.Count;
        if (count > MaxInterfaceMembers)
            ctx.ReportDiagnostic(Diagnostic.Create(InterfaceTooWideRule, i.Identifier.GetLocation(), i.Identifier.Text, count));
    }
}
