using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Stellar.Analyzers.Tests;

// CSharpAnalyzerVerifier<,>.Test is not available in the 1.1.2 package;
// use CSharpAnalyzerTest<,> directly (same API, same markup support).
using AnalyzerTest = CSharpAnalyzerTest<
    Stellar.Analyzers.SizeAndShapeAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

public sealed class SizeAndShapeAnalyzerTests
{
    private static string Lines(int n)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < n; i++) sb.Append("        var x").Append(i).Append(" = ").Append(i).Append(";\n");
        return sb.ToString();
    }

    [Fact]
    public async Task Method_Over50_Lines_Reports_Major()
    {
        var src = $@"
class C
{{
    void {{|STELLAR0002:M|}}()
    {{
{Lines(60)}    }}
}}";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Method_Over100_Lines_Reports_Blocker_And_Major()
    {
        // Both STELLAR0001 and STELLAR0002 are reported on the same identifier span.
        // Use nested markup: outer span carries STELLAR0001, inner span carries STELLAR0002.
        var src = $@"
class C
{{
    void {{|STELLAR0001:{{|STELLAR0002:M|}}|}}()
    {{
{Lines(110)}    }}
}}";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Small_Method_NoDiagnostics()
    {
        // ~25-line block body — well under the 50-line major threshold.
        var src = $@"
class C
{{
    void M()
    {{
{Lines(25)}    }}
}}";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Method_With6Params_ReportsTooMany()
    {
        var src = @"
class C { void {|STELLAR0003:M|}(int a, int b, int c, int d, int e, int f) { } }";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Method_With5Params_Ok()
    {
        var src = @"
class C { void M(int a, int b, int c, int d, int e) { } }";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Ctor_With7Deps_ReportsTooMany()
    {
        var src = @"
class C { public {|STELLAR0004:C|}(int a, int b, int c, int d, int e, int f, int g) { } }";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Ctor_With7Deps_InPluginServices_IsExempt()
    {
        // PluginServices is the aggregator implementation; its ctor is exempt from STELLAR0004.
        var src = @"
class PluginServices { public PluginServices(int a, int b, int c, int d, int e, int f, int g) { } }";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Interface_With9Members_ReportsTooWide()
    {
        var src = @"
interface {|STELLAR0005:IWide|}
{
    void A(); void B(); void C(); void D(); void E();
    void F(); void G(); void H(); void I();
}";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Interface_NamedIPluginServices_IsExempt()
    {
        var src = @"
interface IPluginServices
{
    void A(); void B(); void C(); void D(); void E();
    void F(); void G(); void H(); void I(); void J();
}";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Interface_NamedIThemeLayout_NoLongerExempt()
    {
        // IThemeLayout deleted in Phase 2 (A-04); exemption removed from analyzer.
        // An interface named IThemeLayout with >8 members now fires STELLAR0005.
        var src = @"
interface {|STELLAR0005:IThemeLayout|}
{
    void A(); void B(); void C(); void D(); void E();
    void F(); void G(); void H(); void I(); void J();
    void K();
}";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    // ── Boundary tests (pin off-by-one behaviour) ────────────────────────────

    [Fact]
    public async Task Method_Exactly50Lines_NoDiagnostic()
    {
        // Body spans exactly 50 lines (brace-to-brace inclusive) — at threshold, no diagnostic.
        var src = $@"
class C
{{
    void M()
    {{
{Lines(48)}    }}
}}";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Method_Exactly51Lines_ReportsMajor()
    {
        // Body spans exactly 51 lines — one past the threshold, STELLAR0002 fires.
        var src = $@"
class C
{{
    void {{|STELLAR0002:M|}}()
    {{
{Lines(49)}    }}
}}";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Ctor_With6Deps_Ok()
    {
        var src = @"
class C { public C(int a, int b, int c, int d, int e, int f) { } }";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task Interface_With8Members_Ok()
    {
        var src = @"
interface IEight { void A(); void B(); void C(); void D(); void E(); void F(); void G(); void H(); }";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }

    [Fact]
    public async Task LocalFunction_With6Params_ReportsTooMany()
    {
        var src = @"
class C
{
    void M()
    {
        void {|STELLAR0003:Local|}(int a, int b, int c, int d, int e, int f) { }
    }
}";
        await new AnalyzerTest { TestCode = src, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();
    }
}
