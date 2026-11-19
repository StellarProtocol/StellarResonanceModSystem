using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Stellar.Analyzers;

/// <summary>
/// STELLAR0007 — legacy uGUI <c>UnityEngine.UI.Text</c> is created and written ONLY through the framework's one text
/// path (<c>LegacyText</c> in Stellar.Infrastructure), which routes a Thai string to the TMP twin in the shipped Thai
/// face (<c>ThaiTmpTwin</c>). A raw <c>AddComponent&lt;Text&gt;()</c> or <c>text.text = …</c> elsewhere is how Thai
/// slipped past on ~35 sites (fw issue #169), so the build fails on one.
/// <para>Flags: a generic <c>AddComponent&lt;T&gt;()</c> call whose T is (or derives from) <c>UnityEngine.UI.Text</c>,
/// and an assignment (= / +=) to the <c>text</c> property declared on such a type. Not flagged: TMP's <c>text</c>,
/// <c>InputField.text</c>, reads.</para>
/// <para>Allowed inside any method, property, constructor or type (or a containing type) marked
/// <c>[LegacyTextAllowed("reason")]</c> — the LegacyText helper itself, the measuring helpers, and justified
/// fixed-glyph / digits-only sites. Semantic (needs the bound type); matching is by full type name, so the rule works
/// against both the Unity reference assemblies and the IL2CPP interop ones.</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LegacyTextAnalyzer : DiagnosticAnalyzer
{
    private const string TextType = "UnityEngine.UI.Text";
    private const string AllowAttribute = "LegacyTextAllowed";

    internal static readonly DiagnosticDescriptor Rule = DiagnosticIds.MakeError(
        DiagnosticIds.LegacyTextOutsideHelper, "Legacy uGUI Text outside the Thai-aware text path",
        "{0} bypasses the Thai text path: create with LegacyText.Add and write with LegacyText.Set / SetStatic / SetAscii, or mark the member [LegacyTextAllowed(\"reason\")]");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression, SyntaxKind.AddAssignmentExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx)
    {
        var inv = (InvocationExpressionSyntax)ctx.Node;
        var name = inv.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name,
            SimpleNameSyntax s => s,
            _ => null,
        };
        if (name is not GenericNameSyntax g || g.Identifier.Text != "AddComponent") return;
        if (ctx.SemanticModel.GetSymbolInfo(inv, ctx.CancellationToken).Symbol is not IMethodSymbol ms) return;
        if (ms.TypeArguments.Length != 1 || !IsText(ms.TypeArguments[0])) return;
        if (Allowed(inv, ctx.SemanticModel)) return;
        ctx.ReportDiagnostic(Diagnostic.Create(Rule, inv.GetLocation(), "AddComponent<Text>()"));
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext ctx)
    {
        var a = (AssignmentExpressionSyntax)ctx.Node;
        if (a.Left is not MemberAccessExpressionSyntax m || m.Name.Identifier.Text != "text") return;
        if (ctx.SemanticModel.GetSymbolInfo(m, ctx.CancellationToken).Symbol is not IPropertySymbol p) return;
        if (!IsText(p.ContainingType)) return;
        if (Allowed(a, ctx.SemanticModel)) return;
        ctx.ReportDiagnostic(Diagnostic.Create(Rule, a.GetLocation(), "Writing Text.text"));
    }

    private static bool IsText(ITypeSymbol? t)
    {
        for (; t != null; t = t.BaseType)
            if (t.ToDisplayString() == TextType) return true;
        return false;
    }

    // [LegacyTextAllowed] on the enclosing member (method / ctor / property / local function) or any enclosing type.
    private static bool Allowed(SyntaxNode node, SemanticModel model)
    {
        foreach (var anc in node.Ancestors())
        {
            SyntaxList<AttributeListSyntax> lists = anc switch
            {
                BaseMethodDeclarationSyntax b => b.AttributeLists,
                BasePropertyDeclarationSyntax pr => pr.AttributeLists,
                LocalFunctionStatementSyntax lf => lf.AttributeLists,
                TypeDeclarationSyntax td => td.AttributeLists,
                _ => default,
            };
            if (lists.SelectMany(l => l.Attributes).Any(IsAllowAttribute)) return true;
        }
        return false;
    }

    private static bool IsAllowAttribute(AttributeSyntax a)
    {
        var n = a.Name switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            _ => a.Name.ToString(),
        };
        return n is AllowAttribute or AllowAttribute + "Attribute";
    }
}
