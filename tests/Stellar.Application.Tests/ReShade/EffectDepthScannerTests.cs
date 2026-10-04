using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class EffectDepthScannerTests
{
    [Fact]
    public void UsesDepth_true_for_ReShade_DepthBuffer_token()
    {
        const string fx = "float depth = ReShade::DepthBuffer.Sample(s, uv).x;";
        Assert.True(EffectDepthScanner.UsesDepth(fx));
    }

    [Fact]
    public void UsesDepth_true_for_GetLinearizedDepth_token()
    {
        const string fx = "float depth = ReShade::GetLinearizedDepth(uv);";
        Assert.True(EffectDepthScanner.UsesDepth(fx));
    }

    [Fact]
    public void UsesDepth_true_for_texture_depth_semantic()
    {
        const string fx = "texture2D t : DEPTH;";
        Assert.True(EffectDepthScanner.UsesDepth(fx));
    }

    [Fact]
    public void UsesDepth_false_when_the_mention_is_only_inside_a_line_comment()
    {
        const string fx = "// this effect used to read ReShade::DepthBuffer, not anymore\n" +
                           "float4 main() { return float4(1, 1, 1, 1); }";
        Assert.False(EffectDepthScanner.UsesDepth(fx));
    }

    [Fact]
    public void UsesDepth_false_for_an_unrelated_effect()
    {
        const string fx = "float4 main(float2 uv : TEXCOORD) : SV_Target\n" +
                           "{\n" +
                           "    return tex2D(ReShade::BackBuffer, uv);\n" +
                           "}";
        Assert.False(EffectDepthScanner.UsesDepth(fx));
    }

    [Fact]
    public void UsesDepth_true_even_when_an_unrelated_comment_precedes_a_real_reference()
    {
        const string fx = "// just a bloom pass\n" +
                           "float depth = ReShade::GetLinearizedDepth(uv);";
        Assert.True(EffectDepthScanner.UsesDepth(fx));
    }
    [Theory]
    [InlineData("texture2D t : depth;")]
    [InlineData("texture2D t : Depth;")]
    [InlineData("float d = reshade::depthbuffer.Sample(s, uv).x;")]
    [InlineData("float d = ReShade::GETLINEARIZEDDEPTH(uv);")]
    public void UsesDepth_is_case_insensitive(string fx)
    {
        Assert.True(EffectDepthScanner.UsesDepth(fx));
    }

    [Theory]
    [InlineData("float main() : SV_Depth { return 0; }")]
    [InlineData("float4 main() : SV_DEPTH { return 0; }")]
    [InlineData("float main() : sv_depth { return 0; }")]
    public void UsesDepth_false_for_the_system_value_depth_OUTPUT_in_any_case(string fx)
    {
        Assert.False(EffectDepthScanner.UsesDepth(fx));
    }

    [Theory]
    [InlineData("float d = tex2D(DepthBuffer, uv).x;")]
    [InlineData("sampler2D sDepth { Texture = DEPTHBUFFERTEX; };")]
    public void UsesDepth_true_for_a_DepthBuffer_reference_without_the_namespace(string fx)
    {
        Assert.True(EffectDepthScanner.UsesDepth(fx));
    }

    [Fact]
    public void UsesDepth_false_for_null_source()
    {
        Assert.False(EffectDepthScanner.UsesDepth(null));
    }

    // A pack may carry its own copy of ReShade's standard header (AcerolaFX_Common.fxh): a `namespace ReShade { … }`
    // block that DECLARES the depth texture and DEFINES GetLinearizedDepth. Declarations are not use — every effect
    // including that header was marked depth, so shaped photos dropped the whole pack (2026-10-04).
    private const string OwnReShadeNamespace =
        "namespace ReShade\n{\n" +
        "    texture DepthBufferTex : DEPTH;\n" +
        "    sampler DepthBuffer { Texture = DepthBufferTex; };\n" +
        "    float GetLinearizedDepth(float2 texcoord)\n    {\n" +
        "        float depth = tex2Dlod(DepthBuffer, float4(texcoord, 0, 0)).x; /* { brace in a comment */\n" +
        "        return depth;\n    }\n}\n";

    [Fact]
    public void UsesDepth_false_when_depth_is_only_declared_inside_a_packs_own_ReShade_namespace()
    {
        var fx = OwnReShadeNamespace + "float4 main(float2 uv : TEXCOORD) : SV_Target { return tex2D(ReShade::BackBuffer, uv); }";
        Assert.False(EffectDepthScanner.UsesDepth(fx));
    }

    [Fact]
    public void UsesDepth_true_when_an_effect_reads_the_depth_its_own_ReShade_namespace_declares()
    {
        var fx = OwnReShadeNamespace + "float4 main(uint2 id : SV_POSITION) : SV_Target { return tex2Dfetch(ReShade::DepthBuffer, id).r; }";
        Assert.True(EffectDepthScanner.UsesDepth(fx));
    }

    [Fact]
    public void UsesDepth_still_sees_depth_in_other_namespaces()
    {
        const string fx = "namespace Common { float d(float2 uv) { return ReShade::GetLinearizedDepth(uv); } }";
        Assert.True(EffectDepthScanner.UsesDepth(fx));
    }
}
