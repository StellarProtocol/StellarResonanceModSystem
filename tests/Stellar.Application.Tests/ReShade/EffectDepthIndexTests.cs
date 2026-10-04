using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class EffectDepthIndexTests
{
    private readonly InMemoryEffectFiles _fs = new();
    private readonly EffectDepthIndex _index;

    public EffectDepthIndexTests()
    {
        _index = new EffectDepthIndex(_fs);
        _index.SetSearchPaths(new[] { "/pack/**" });
        _fs.Add("/pack/Shaders/ReShade.fxh", "namespace ReShade { texture DepthBufferTex : DEPTH; float GetLinearizedDepth(float2 uv) { return 0; } }");
        _fs.Add("/pack/Shaders/PackLib.fxh", "float4 Tone(float4 c) { return c; }");
        _fs.Add("/pack/Shaders/Plain.fx", "#include \"ReShade.fxh\"\n#include \"PackLib.fxh\"\nfloat4 PS() { return tex2D(ReShade::BackBuffer, uv); }");
        _fs.Add("/pack/Shaders/Deep.fx", "#include \"DepthLib.fxh\"");
        _fs.Add("/pack/Shaders/lib/DepthLib.fxh", "float d = ReShade::GetLinearizedDepth(uv);");
    }

    [Fact]
    public void An_effect_without_depth_reads_false()
    {
        Assert.False(_index.Resolve("Plain.fx"));
        Assert.False(_index.Known("Plain.fx"));
    }

    [Fact]
    public void Depth_use_in_an_included_file_reads_true()
    {
        Assert.True(_index.Resolve("Deep.fx"));
    }

    [Fact]
    public void An_effect_that_cannot_be_found_counts_as_using_depth()
    {
        Assert.True(_index.Resolve("Elsewhere.fx"));
    }

    [Fact]
    public void An_effect_that_cannot_be_read_counts_as_using_depth()
    {
        _fs.MarkUnreadable("/pack/Shaders/Plain.fx");
        Assert.True(_index.Resolve("Plain.fx"));
    }

    [Fact]
    public void An_effect_that_only_includes_ReShade_fxh_and_never_reads_depth_is_false()
    {
        _fs.Add("/pack/Shaders/Bloom.fx", "#include \"ReShade.fxh\"\nfloat4 PS() { return tex2D(ReShade::BackBuffer, uv); }");
        Assert.False(_index.Resolve("Bloom.fx"));
        Assert.Equal(0, _fs.ReadsOf("/pack/Shaders/ReShade.fxh"));
    }

    [Fact]
    public void An_effect_calling_GetLinearizedDepth_is_true()
    {
        _fs.Add("/pack/Shaders/MXAO.fx", "#include \"ReShade.fxh\"\nfloat d = ReShade::GetLinearizedDepth(uv);");
        Assert.True(_index.Resolve("MXAO.fx"));
    }

    [Fact]
    public void An_unreadable_include_counts_as_using_depth()
    {
        _fs.MarkUnreadable("/pack/Shaders/PackLib.fxh");
        Assert.True(_index.Resolve("Plain.fx"));
    }

    [Fact]
    public void Unknown_until_resolved_and_resolved_once()
    {
        Assert.Null(_index.Known("Plain.fx"));
        Assert.True(_index.NeedsWork("Plain.fx"));
        _index.Resolve("Plain.fx");
        Assert.False(_index.NeedsWork("Plain.fx"));
        Assert.Equal(1, _fs.ReadsOf("/pack/Shaders/Plain.fx"));
    }

    [Fact]
    public void A_shared_include_is_read_once_across_effects()
    {
        _fs.Add("/pack/Shaders/Other.fx", "#include \"PackLib.fxh\"");
        _index.Resolve("Plain.fx");
        _index.Resolve("Other.fx");
        Assert.Equal(1, _fs.ReadsOf("/pack/Shaders/PackLib.fxh"));
    }

    [Fact]
    public void Stale_entries_keep_their_value_and_skip_rereading_when_no_file_changed()
    {
        _index.Resolve("Plain.fx");
        _index.MarkStale();
        Assert.True(_index.NeedsWork("Plain.fx"));
        Assert.False(_index.Known("Plain.fx"));
        Assert.False(_index.Resolve("Plain.fx"));
        Assert.Equal(1, _fs.ReadsOf("/pack/Shaders/Plain.fx"));
    }

    [Fact]
    public void A_changed_include_is_reread_after_a_reload()
    {
        Assert.False(_index.Resolve("Plain.fx"));
        _fs.Touch("/pack/Shaders/PackLib.fxh", "float4 Tone(float4 c) { return ReShade::GetLinearizedDepth(c.xy); }");
        _index.MarkStale();
        Assert.True(_index.Resolve("Plain.fx"));
    }

    [Fact]
    public void An_effect_not_found_before_a_reload_is_searched_again_after_it()
    {
        Assert.True(_index.Resolve("Late.fx"));
        _fs.Add("/pack/Shaders/Late.fx", "float4 PS() { return 0; }");
        _index.MarkStale();
        Assert.False(_index.Resolve("Late.fx"));
    }

    [Fact]
    public void The_file_index_is_walked_once_and_rebuilt_after_a_reload()
    {
        _index.Resolve("Plain.fx");
        _index.Resolve("Deep.fx");
        _index.Resolve("Elsewhere.fx");
        Assert.Equal(1, _fs.WalksOf("/pack"));
        _index.MarkStale();
        _index.Resolve("Elsewhere.fx");
        Assert.Equal(2, _fs.WalksOf("/pack"));
    }

    [Fact]
    public void A_self_include_through_dot_dot_is_caught_as_a_cycle_on_the_normalized_path()
    {
        _fs.Add("/pack/Shaders/Loop.fx", "#include \"../Shaders/PackLib.fxh\"\n#include \"../Shaders/Loop.fx\"\nfloat4 PS() { return 0; }");
        Assert.False(_index.Resolve("Loop.fx"));
        Assert.Equal(1, _fs.ReadsOf("/pack/Shaders/Loop.fx"));
    }

    [Fact]
    public void A_rooted_include_counts_as_using_depth()
    {
        _fs.Add("/pack/Shaders/Abs.fx", "#include \"/pack/Shaders/PackLib.fxh\"");
        Assert.True(_index.Resolve("Abs.fx"));
    }

    [Fact]
    public void New_search_paths_forget_everything()
    {
        _index.Resolve("Plain.fx");
        _index.SetSearchPaths(new[] { "/other/**" });
        Assert.Null(_index.Known("Plain.fx"));
        Assert.True(_index.Resolve("Plain.fx"));
    }

    // Item 0 (D8 under-flag): two packs under one ** root each ship a Common.fxh and only one reads depth. Whichever the
    // walk lists first, the effect must be flagged — every candidate is scanned and the results are ORed.
    [Theory]
    [InlineData("packA")]
    [InlineData("packB")]
    public void An_ambiguous_include_flags_depth_when_any_candidate_reads_it_regardless_of_order(string depthPack)
    {
        var fs = new InMemoryEffectFiles();
        var index = new EffectDepthIndex(fs);
        index.SetSearchPaths(new[] { "/two/**" });
        fs.Add("/two/fx/Look.fx", "#include \"Common.fxh\"\nfloat4 PS() { return Tone(c); }");
        foreach (var pack in new[] { "packA", "packB" })
        {
            fs.Add($"/two/{pack}/Common.fxh", pack == depthPack
                ? "float4 Tone(float4 c) { return ReShade::GetLinearizedDepth(c.xy); }"
                : "float4 Tone(float4 c) { return c; }");
        }
        Assert.True(index.Resolve("Look.fx"));
    }

    [Fact]
    public void An_ambiguous_include_where_no_candidate_reads_depth_stays_false()
    {
        var fs = new InMemoryEffectFiles();
        var index = new EffectDepthIndex(fs);
        index.SetSearchPaths(new[] { "/two/**" });
        fs.Add("/two/fx/Look.fx", "#include \"Common.fxh\"");
        fs.Add("/two/packA/Common.fxh", "float4 A;");
        fs.Add("/two/packB/Common.fxh", "float4 B;");
        Assert.False(index.Resolve("Look.fx"));
    }

    [Theory]
    [InlineData("packA")]
    [InlineData("packB")]
    public void Duplicate_effect_files_are_all_scanned_and_ORed(string depthPack)
    {
        var fs = new InMemoryEffectFiles();
        var index = new EffectDepthIndex(fs);
        index.SetSearchPaths(new[] { "/two/**" });
        foreach (var pack in new[] { "packA", "packB" })
            fs.Add($"/two/{pack}/Dup.fx", pack == depthPack ? "float d = ReShade::GetLinearizedDepth(uv);" : "float4 PS() { return 0; }");
        Assert.True(index.Resolve("Dup.fx"));
    }
}
