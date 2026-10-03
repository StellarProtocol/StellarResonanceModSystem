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
}
