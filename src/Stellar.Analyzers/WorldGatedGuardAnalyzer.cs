using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Stellar.Analyzers;

/// <summary>
/// STELLAR0006 — a method marked <c>[WorldGated]</c> touches live game state and MUST self-gate on
/// <c>IsWorldActive</c>. The rule fails the build if such a method has no <c>if (!…IsWorldActive) return;</c>
/// early-return guard. Purely syntactic (no semantic model): it looks for a leading <c>if</c> statement whose
/// condition mentions <c>IsWorldActive</c> and whose then-branch returns. Position-lenient by design — it
/// verifies the guard is <i>present</i> (see docs/game-phases-design.md §5.2); it does not enforce that the
/// guard is literally the first statement. Runs on <c>src/</c> only (framework projects), never plugins.
///
/// Roslyn discovers analyzers via <see cref="DiagnosticAnalyzerAttribute"/>; this class must be <c>public</c>.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class WorldGatedGuardAnalyzer : DiagnosticAnalyzer
{
    private const string GuardMemberName = "IsWorldActive";

    internal static readonly DiagnosticDescriptor MissingGuardRule = DiagnosticIds.MakeError(
        DiagnosticIds.WorldGatedMissingGuard, "[WorldGated] method missing IsWorldActive guard",
        "Method '{0}' is [WorldGated] but has no 'if (!...IsWorldActive) return;' guard; add it as an early return");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(MissingGuardRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext ctx)
    {
        var m = (MethodDeclarationSyntax)ctx.Node;
        if (!HasWorldGatedAttribute(m)) return;
        if (m.Body is { } body && body.Statements.Any(IsWorldActiveGuard)) return;
        ctx.ReportDiagnostic(Diagnostic.Create(MissingGuardRule, m.Identifier.GetLocation(), m.Identifier.Text));
    }

    private static bool HasWorldGatedAttribute(MethodDeclarationSyntax m) =>
        m.AttributeLists.SelectMany(a => a.Attributes).Any(a => SimpleName(a) is "WorldGated" or "WorldGatedAttribute");

    private static string SimpleName(AttributeSyntax a) => a.Name switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        _ => a.Name.ToString(),
    };

    // A guard is `if (<cond mentioning IsWorldActive>) <return / block-with-return>`.
    private static bool IsWorldActiveGuard(StatementSyntax s)
    {
        if (s is not IfStatementSyntax ifs) return false;
        var mentionsGuard = ifs.Condition.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>().Any(n => n.Identifier.Text == GuardMemberName);
        return mentionsGuard && ReturnsEarly(ifs.Statement);
    }

    private static bool ReturnsEarly(StatementSyntax then) => then switch
    {
        ReturnStatementSyntax => true,
        BlockSyntax b => b.Statements.OfType<ReturnStatementSyntax>().Any(),
        _ => false,
    };
}
