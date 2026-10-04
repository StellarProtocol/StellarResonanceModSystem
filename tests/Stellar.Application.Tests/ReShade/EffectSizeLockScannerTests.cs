using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

// A texture sized from the screen is created ONCE (by the screen-size permutation) and reused by every other size's
// permutation, so a larger capture reads its top-left corner magnified. Such an effect is "size-locked".
public sealed class EffectSizeLockScannerTests
{
    [Fact]
    public void A_BUFFER_sized_texture_is_locked()
    {
        const string fx = "texture2D BloomTex { Width = BUFFER_WIDTH; Height = BUFFER_HEIGHT; Format = RGBA16F; };";
        Assert.True(EffectSizeLockScanner.IsSizeLocked(fx));
    }

    [Theory]
    [InlineData("texture t { Width = BUFFER_WIDTH / 2; Height = 64; };")]
    [InlineData("texture t { Width = 64; Height = BUFFER_HEIGHT/TILE_SIZE; };")]
    [InlineData("texture2D t < pooled = true; > { Width = BUFFER_WIDTH; Height = BUFFER_HEIGHT; };")]
    [InlineData("texture t { Width = CA * ( max( BUFFER_WIDTH, BUFFER_HEIGHT ) / 1920.0f ); Height = 4; };")]
    [InlineData("texture t { Width = BUFFER_SCREEN_SIZE.x; Height = 1; };")]
    [InlineData("texture t { Width = ReShade::ScreenSize.x / 4; Height = 1; };")]
    [InlineData("texture t\n{\n    Width = BUFFER_WIDTH;\n    Height = BUFFER_HEIGHT;\n};")]
    public void Arithmetic_on_a_screen_size_token_is_locked(string fx)
    {
        Assert.True(EffectSizeLockScanner.IsSizeLocked(fx));
    }

    [Fact]
    public void A_macro_that_expands_to_the_screen_size_is_locked()
    {
        const string fx = "#define HALF_W (BUFFER_WIDTH / 2)\n" +
                          "#ifndef SWIDTH\n  #define SWIDTH HALF_W\n#endif\n" +
                          "texture t { Width = SWIDTH; Height = 32; };";
        Assert.True(EffectSizeLockScanner.IsSizeLocked(fx));
    }

    [Fact]
    public void A_static_const_sized_from_the_screen_is_locked()
    {
        const string fx = "static const int2 NormalResolution = int2(BUFFER_WIDTH / 2, BUFFER_HEIGHT / 2);\n" +
                          "texture t { Width = NormalResolution.x; Height = NormalResolution.y; };";
        Assert.True(EffectSizeLockScanner.IsSizeLocked(fx));
    }

    [Theory]
    [InlineData("texture2D LutTex < source = \"lut.png\"; > { Width = 1024; Height = 32; Format = RGBA8; };")]
    [InlineData("#define LUT_SIZE 32\ntexture t { Width = LUT_SIZE * LUT_SIZE; Height = LUT_SIZE; };")]
    [InlineData("texture t { Width = 256; Height = 256; };")]
    public void A_fixed_size_texture_is_not_locked(string fx)
    {
        Assert.False(EffectSizeLockScanner.IsSizeLocked(fx));
    }

    [Theory]
    [InlineData("texture BackBufferTex : COLOR;")]
    [InlineData("texture DepthBufferTex : DEPTH;")]
    [InlineData("texture2D t : COLOR { Width = BUFFER_WIDTH; Height = BUFFER_HEIGHT; };")]
    public void The_COLOR_and_DEPTH_semantic_textures_are_not_locked(string fx)
    {
        Assert.False(EffectSizeLockScanner.IsSizeLocked(fx));
    }

    [Fact]
    public void A_single_pass_effect_that_only_reads_the_back_buffer_is_not_locked()
    {
        const string fx = "float4 PS(float4 p : SV_Position, float2 uv : TEXCOORD) : SV_Target\n" +
                          "{ return tex2D(ReShade::BackBuffer, uv) * BUFFER_WIDTH / BUFFER_WIDTH; }";
        Assert.False(EffectSizeLockScanner.IsSizeLocked(fx));
    }

    [Theory]
    [InlineData("// texture t { Width = BUFFER_WIDTH; Height = BUFFER_HEIGHT; };\nfloat4 PS() { return 0; }")]
    [InlineData("/* texture t { Width = BUFFER_WIDTH; Height = BUFFER_HEIGHT; }; */ float4 PS() { return 0; }")]
    [InlineData("texture t { Width = 64; /* was BUFFER_WIDTH */ Height = 64; // BUFFER_HEIGHT\n };")]
    public void A_mention_only_inside_a_comment_is_not_locked(string fx)
    {
        Assert.False(EffectSizeLockScanner.IsSizeLocked(fx));
    }

    [Fact]
    public void A_screen_sized_macro_that_no_texture_uses_is_not_locked()
    {
        const string fx = "#define HALF_W (BUFFER_WIDTH / 2)\ntexture t { Width = 16; Height = 16; };\nfloat x = HALF_W;";
        Assert.False(EffectSizeLockScanner.IsSizeLocked(fx));
    }

    [Fact]
    public void A_self_referencing_macro_does_not_loop()
    {
        const string fx = "#define A B\n#define B A\ntexture t { Width = A; Height = 1; };";
        Assert.False(EffectSizeLockScanner.IsSizeLocked(fx));
    }

    [Fact]
    public void Null_is_false_the_caller_decides_for_unreadable_sources()
    {
        Assert.False(EffectSizeLockScanner.IsSizeLocked(null));
    }
}
