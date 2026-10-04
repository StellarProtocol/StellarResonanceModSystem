using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

// "Temporal" = the effect's output depends on earlier frames (adaptation, accumulation, history), so a fresh isolated
// runtime must render it for a while before the photo.
public sealed class EffectTemporalScannerTests
{
    [Theory]
    [InlineData("uniform float frametime < source = \"frametime\"; >;")]   // prod80 Bloom, AutoExposure, NeoBloom
    [InlineData("uniform float uTime <source = \"timer\";>;")]
    [InlineData("uniform int frameCount < source = \"framecount\"; >;")]
    [InlineData("uniform float t < source = \"FrameTime\"; >;")]
    public void A_frame_time_uniform_is_temporal(string fx) => Assert.True(EffectTemporalScanner.IsTemporal(fx));

    [Theory]
    [InlineData("texture texBPrevAvgLuma { Format = R16F; };")]
    [InlineData("texture2D tArcaneBloom_LastAdapt { Width = 1; Height = 1; };")]
    [InlineData("texture HistoryTex { Width = BUFFER_WIDTH; Height = BUFFER_HEIGHT; };")]
    [InlineData("texture2D AccumTex { Format = RGBA16F; };")]
    public void A_history_texture_is_temporal(string fx) => Assert.True(EffectTemporalScanner.IsTemporal(fx));

    [Theory]
    [InlineData("uniform float2 m < source = \"mousepoint\"; >;\nfloat4 PS() { return tex2D(ReShade::BackBuffer, uv); }")]
    [InlineData("texture2D LutTex < source = \"lut.png\"; > { Width = 1024; Height = 32; };")]
    [InlineData("texture BloomTex { Width = BUFFER_WIDTH / 2; Height = BUFFER_HEIGHT / 2; };")]
    [InlineData("// uniform float frametime < source = \"frametime\"; >;\nfloat4 PS() { return 0; }")]
    [InlineData("/* texture texBPrevAvgLuma { Format = R16F; }; */")]
    public void A_stateless_effect_is_not_temporal(string fx) => Assert.False(EffectTemporalScanner.IsTemporal(fx));

    [Fact]
    public void Null_is_false_the_caller_decides_for_unreadable_sources() =>
        Assert.False(EffectTemporalScanner.IsTemporal(null));
}
