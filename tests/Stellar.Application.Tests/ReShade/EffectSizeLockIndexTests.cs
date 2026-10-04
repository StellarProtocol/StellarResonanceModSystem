using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class EffectSizeLockIndexTests
{
    private readonly InMemoryEffectFiles _fs = new();
    private readonly EffectSizeLockIndex _index;

    public EffectSizeLockIndexTests()
    {
        _index = new EffectSizeLockIndex(_fs);
        _index.SetSearchPaths(new[] { "/pack/**" });
        _fs.Add("/pack/Shaders/ReShade.fxh", "#define BUFFER_SCREEN_SIZE float2(BUFFER_WIDTH, BUFFER_HEIGHT)\n" +
                                             "namespace ReShade { texture BackBufferTex : COLOR; texture DepthBufferTex : DEPTH; }");
        _fs.Add("/pack/Shaders/Plain.fx", "#include \"ReShade.fxh\"\nfloat4 PS() { return tex2D(ReShade::BackBuffer, uv); }");
        _fs.Add("/pack/Shaders/Bloom.fx", "#include \"ReShade.fxh\"\n#include \"BloomLib.fxh\"\nfloat4 PS() { return Bloom(uv); }");
        _fs.Add("/pack/Shaders/lib/BloomLib.fxh", "#define DOWN 4\ntexture BloomTex { Width = BUFFER_WIDTH / DOWN; Height = BUFFER_HEIGHT / DOWN; };");
        _fs.Add("/pack/Shaders/Lut.fx", "texture LutTex < source = \"lut.png\"; > { Width = 1024; Height = 32; };");
    }

    [Fact]
    public void A_screen_sized_texture_in_an_included_file_is_locked()
    {
        Assert.True(_index.Resolve("Bloom.fx"));
        Assert.True(_index.Known("Bloom.fx"));
    }

    [Fact]
    public void A_back_buffer_only_effect_is_not_locked_and_ReShade_fxh_is_not_read()
    {
        Assert.False(_index.Resolve("Plain.fx"));
        Assert.Equal(0, _fs.ReadsOf("/pack/Shaders/ReShade.fxh"));
    }

    [Fact]
    public void A_fixed_size_texture_is_not_locked()
    {
        Assert.False(_index.Resolve("Lut.fx"));
    }

    [Fact]
    public void An_effect_that_cannot_be_found_or_read_counts_as_locked()
    {
        Assert.True(_index.Resolve("Elsewhere.fx"));
        _fs.MarkUnreadable("/pack/Shaders/lib/BloomLib.fxh");
        _fs.Add("/pack/Shaders/Other.fx", "#include \"BloomLib.fxh\"");
        Assert.True(_index.Resolve("Other.fx"));
    }

    [Fact]
    public void Unknown_until_resolved()
    {
        Assert.Null(_index.Known("Lut.fx"));
        Assert.True(_index.NeedsWork("Lut.fx"));
        _index.Resolve("Lut.fx");
        Assert.False(_index.NeedsWork("Lut.fx"));
    }
}
