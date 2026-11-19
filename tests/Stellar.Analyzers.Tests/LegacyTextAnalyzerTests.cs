using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Stellar.Analyzers.Tests;

using LegacyTextTest = CSharpAnalyzerTest<
    Stellar.Analyzers.LegacyTextAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

// STELLAR0007: legacy uGUI Text is created / written only through LegacyText (Thai-aware) or a [LegacyTextAllowed] member.
public sealed class LegacyTextAnalyzerTests
{
    // Minimal stand-ins for the Unity / TMP types the rule binds to (matched by full name).
    private const string Stubs = @"
namespace UnityEngine { public class Component { } public class GameObject { public T AddComponent<T>() where T : new() => new T(); } }
namespace UnityEngine.UI { public class Text : UnityEngine.Component { public string text { get; set; } } public class SubText : Text { }
    public class InputField : UnityEngine.Component { public string text { get; set; } } }
namespace TMPro { public class TMP_Text : UnityEngine.Component { public string text { get; set; } } }
sealed class LegacyTextAllowedAttribute : System.Attribute { public LegacyTextAllowedAttribute(string reason) { } }
";

    private static Task Run(string code) =>
        new LegacyTextTest { TestCode = Stubs + code, ReferenceAssemblies = ReferenceAssemblies.Net.Net80 }.RunAsync();

    [Fact]
    public Task AddComponent_Text_Reports() => Run(@"
class C { void M(UnityEngine.GameObject go) { var t = {|STELLAR0007:go.AddComponent<UnityEngine.UI.Text>()|}; } }");

    [Fact]
    public Task AddComponent_Derived_Text_Reports() => Run(@"
class C { void M(UnityEngine.GameObject go) { {|STELLAR0007:go.AddComponent<UnityEngine.UI.SubText>()|}; } }");

    [Fact]
    public Task Text_Write_Reports() => Run(@"
class C { void M(UnityEngine.UI.Text t) { {|STELLAR0007:t.text = ""a""|}; {|STELLAR0007:t.text += ""b""|}; } }");

    [Fact]
    public Task Text_Read_And_Other_Text_Properties_Ok() => Run(@"
class C { void M(UnityEngine.UI.Text t, UnityEngine.UI.InputField f, TMPro.TMP_Text tmp, UnityEngine.GameObject go)
{ var s = t.text; f.text = s; tmp.text = s; go.AddComponent<UnityEngine.UI.InputField>(); } }");

    [Fact]
    public Task Allowed_On_Method_Or_Type_Ok() => Run(@"
class C { [LegacyTextAllowed(""digits only"")] void M(UnityEngine.UI.Text t) { t.text = ""1""; } }
[LegacyTextAllowed(""the helper"")] static class LegacyText
{ public static UnityEngine.UI.Text Add(UnityEngine.GameObject go) => go.AddComponent<UnityEngine.UI.Text>();
  public static string P { set { } } static void N(UnityEngine.UI.Text t) { t.text = ""x""; } }");

    [Fact]
    public Task Allowed_Does_Not_Leak_To_Siblings() => Run(@"
class C { [LegacyTextAllowed(""x"")] void A(UnityEngine.UI.Text t) { t.text = ""1""; }
          void B(UnityEngine.UI.Text t) { {|STELLAR0007:t.text = ""2""|}; } }");
}
