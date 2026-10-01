using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Photo;

public sealed class RenderQualityServiceTests
{
    // A game with a 1.0 render scale, AA off, and the game's 2048 / 2 cascades / hard-shadow preset.
    internal sealed class FakeBackend : IRenderQualityBackend
    {
        public float Scale = 1.0f;
        public bool Taa;
        public ShadowValues Shadows = new(2048, 2, false);
        public bool Readable = true;
        public readonly List<string> Writes = new();
        public RenderQualityCapabilities Capabilities { get; set; } = new(true, true, true);
        public float? ReadRenderScale() => Readable ? Scale : null;
        public void WriteRenderScale(float scale) { Writes.Add($"scale={scale}"); Scale = scale; }
        public bool? ReadTaa() => Readable ? Taa : null;
        public void WriteTaa(bool on) { Writes.Add($"taa={on}"); Taa = on; }
        public ShadowValues? ReadShadows() => Readable ? Shadows : null;
        public void WriteShadows(ShadowValues values) { Writes.Add($"shadows={values}"); Shadows = values; }
    }

    private static readonly RenderQualityRequest Ss = new() { Supersample = true };
    private static readonly RenderQualityRequest Hs = new() { HighShadows = true };

    [Fact]
    public void Supersample_writes_scale_2_and_taa_on()
    {
        var b = new FakeBackend();
        new RenderQualityService(b).Request(Ss);
        Assert.Equal(2.0f, b.Scale);
        Assert.True(b.Taa);
        Assert.Equal(new ShadowValues(2048, 2, false), b.Shadows);   // shadows untouched
    }

    [Fact]
    public void High_shadows_writes_4096_three_cascades_soft()
    {
        var b = new FakeBackend();
        new RenderQualityService(b).Request(Hs);
        Assert.Equal(new ShadowValues(4096, 3, true), b.Shadows);
        Assert.Equal(1.0f, b.Scale);
        Assert.Single(b.Writes);
    }

    [Fact]
    public void Union_keeps_a_lever_while_any_token_asks_for_it()
    {
        var b = new FakeBackend();
        var s = new RenderQualityService(b);
        var a = s.Request(Ss);
        var c = s.Request(new RenderQualityRequest { Supersample = true, HighShadows = true });
        c.Dispose();
        Assert.Equal(2.0f, b.Scale);                                // a still holds supersampling
        Assert.Equal(new ShadowValues(2048, 2, false), b.Shadows);  // nobody holds shadows any more
        a.Dispose();
        Assert.Equal(1.0f, b.Scale);
    }

    [Fact]
    public void Release_restores_the_game_values_exactly()
    {
        var b = new FakeBackend { Scale = 1.37f, Taa = false, Shadows = new ShadowValues(1024, 1, true) };
        var t = new RenderQualityService(b).Request(new RenderQualityRequest { Supersample = true, HighShadows = true });
        t.Dispose();
        Assert.Equal(1.37f, b.Scale);
        Assert.False(b.Taa);
        Assert.Equal(new ShadowValues(1024, 1, true), b.Shadows);
    }

    [Fact]
    public void Taa_already_on_is_never_written_and_stays_on_after_release()
    {
        var b = new FakeBackend { Taa = true };
        var t = new RenderQualityService(b).Request(Ss);
        t.Dispose();
        Assert.True(b.Taa);
        Assert.DoesNotContain(b.Writes, w => w.StartsWith("taa"));
    }

    [Fact]
    public void Double_dispose_releases_once()
    {
        var b = new FakeBackend();
        var s = new RenderQualityService(b);
        var keep = s.Request(Ss);
        var t = s.Request(Ss);
        t.Dispose();
        t.Dispose();
        Assert.Equal(2.0f, b.Scale);   // the second dispose did not release `keep`'s hold
        keep.Dispose();
        keep.Dispose();
        Assert.Equal(new[] { "scale=2", "taa=True", "scale=1", "taa=False" }, b.Writes);
    }

    [Fact]
    public void Reassert_writes_nothing_when_live_values_already_match()
    {
        var b = new FakeBackend();
        var s = new RenderQualityService(b);
        s.Request(new RenderQualityRequest { Supersample = true, HighShadows = true });
        var count = b.Writes.Count;
        s.Reassert();
        s.Reassert();
        Assert.Equal(count, b.Writes.Count);
    }

    [Fact]
    public void Reassert_rewrites_only_the_lever_the_game_reverted()
    {
        var b = new FakeBackend();
        var s = new RenderQualityService(b);
        s.Request(new RenderQualityRequest { Supersample = true, HighShadows = true });
        b.Writes.Clear();
        b.Scale = 1.0f;   // the game re-applied its grade: render scale only
        s.Reassert();
        Assert.Equal(new[] { "scale=2" }, b.Writes);
    }

    [Fact]
    public void A_value_the_game_writes_while_held_becomes_the_restore_target()
    {
        var b = new FakeBackend();
        var s = new RenderQualityService(b);
        var t = s.Request(new RenderQualityRequest { Supersample = true, HighShadows = true });
        b.Scale = 1.5f;                                   // the player picked 150 % in the settings panel
        b.Shadows = b.Shadows with { Resolution = 1024 }; // the grade rewrote one shadow member only
        s.Reassert();
        Assert.Equal(2.0f, b.Scale);
        Assert.Equal(new ShadowValues(4096, 3, true), b.Shadows);
        t.Dispose();
        Assert.Equal(1.5f, b.Scale);                              // the game's new choice, not the stale 1.0
        Assert.Equal(new ShadowValues(1024, 2, false), b.Shadows); // per member: cascades/soft keep the original
    }

    [Fact]
    public void Release_when_the_game_already_restored_writes_nothing()
    {
        var b = new FakeBackend();
        var t = new RenderQualityService(b).Request(Ss);
        b.Scale = 1.0f;   // the game re-applied its own value just before the release
        b.Writes.Clear();
        t.Dispose();
        Assert.DoesNotContain(b.Writes, w => w.StartsWith("scale"));
    }

    [Fact]
    public void Unreadable_lever_is_applied_on_the_next_reassert()
    {
        var b = new FakeBackend { Readable = false };
        var s = new RenderQualityService(b);
        s.Request(Ss);
        Assert.Empty(b.Writes);
        b.Readable = true;
        s.Reassert();
        Assert.Equal(2.0f, b.Scale);
    }

    [Fact]
    public void A_restore_that_could_not_be_written_is_retried_by_reassert()
    {
        var b = new FakeBackend();
        var s = new RenderQualityService(b);
        var t = s.Request(Ss);
        b.Readable = false;   // mid zone load
        t.Dispose();
        Assert.Equal(2.0f, b.Scale);
        b.Readable = true;
        s.Reassert();
        Assert.Equal(1.0f, b.Scale);
        Assert.False(b.Taa);
    }

    [Fact]
    public void Reassert_with_nothing_held_never_touches_the_game()
    {
        var b = new FakeBackend { Scale = 1.2f };
        new RenderQualityService(b).Reassert();
        Assert.Empty(b.Writes);
    }

    [Fact]
    public void Unload_release_drops_only_that_owners_tokens()
    {
        var b = new FakeBackend();
        var s = new RenderQualityService(b);
        var pluginA = new PluginRenderQuality(s, new object());
        var pluginB = new PluginRenderQuality(s, new object());
        pluginA.Request(new RenderQualityRequest { Supersample = true, HighShadows = true });
        pluginB.Request(Hs);
        pluginA.ReleaseAll();
        Assert.Equal(1.0f, b.Scale);
        Assert.Equal(new ShadowValues(4096, 3, true), b.Shadows);   // B still holds shadows
        pluginB.ReleaseAll();
        Assert.Equal(new ShadowValues(2048, 2, false), b.Shadows);
    }

    [Fact]
    public void Live_reads_the_backend_and_zeroes_unreadable()
    {
        var b = new FakeBackend { Scale = 1.25f, Taa = true };
        var s = new RenderQualityService(b);
        Assert.Equal(new RenderQualityState(1.25f, true, 2048, 2, false), s.Live);
        b.Readable = false;
        Assert.Equal(default, s.Live);
    }

    // ── Capture render-scale guard (spec § 4) ──

    [Fact]
    public void Capture_guard_drops_a_supersampled_scale_to_1_and_restores_2()
    {
        var b = new FakeBackend { Scale = 1.5f };
        var s = new RenderQualityService(b);
        var t = s.Request(Ss);
        var guard = s.SuspendSupersampleForCapture();
        Assert.Equal(1.0f, b.Scale);
        Assert.True(b.Taa);   // TAA stays on
        guard.Dispose();
        Assert.Equal(2.0f, b.Scale);
        t.Dispose();
        Assert.Equal(1.5f, b.Scale);   // the guard's 1.0 was ours — it never became the baseline
    }

    [Fact]
    public void Capture_guard_without_supersampling_writes_nothing()
    {
        var b = new FakeBackend { Scale = 1.5f };
        var s = new RenderQualityService(b);
        s.Request(Hs);
        b.Writes.Clear();
        using (s.SuspendSupersampleForCapture()) { }
        Assert.Empty(b.Writes);
    }

    [Fact]
    public void Capture_guard_double_dispose_is_harmless()
    {
        var b = new FakeBackend();
        var s = new RenderQualityService(b);
        s.Request(Ss);
        var g1 = s.SuspendSupersampleForCapture();
        var g2 = s.SuspendSupersampleForCapture();
        g1.Dispose();
        g1.Dispose();
        Assert.Equal(1.0f, b.Scale);   // g2 still suspends
        g2.Dispose();
        Assert.Equal(2.0f, b.Scale);
    }

    [Fact]
    public void Request_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new RenderQualityService(new FakeBackend()).Request(null!));
    }
}
