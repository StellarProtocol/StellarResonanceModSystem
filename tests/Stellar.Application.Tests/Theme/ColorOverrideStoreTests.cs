using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

public sealed class ColorOverrideStoreTests
{
    [Fact]
    public void Set_Then_TryGet_RoundTrips()
    {
        var s = new ColorOverrideStore(new InMemoryConfigSection());
        s.Set("Sakura", "k", new(0.5f, 0.25f, 1f, 1f));
        Assert.True(s.TryGet("Sakura", "k", out var v));
        Assert.True(System.Math.Abs(0.5f - v.R) < 0.005f);
        Assert.True(System.Math.Abs(0.25f - v.G) < 0.005f);
        Assert.True(System.Math.Abs(1f - v.B) < 0.005f);
    }

    [Fact]
    public void TryGet_MissingKey_False()
    {
        var s = new ColorOverrideStore(new InMemoryConfigSection());
        Assert.False(s.TryGet("Sakura", "nope", out _));
    }

    [Fact]
    public void Clear_RemovesOverride()
    {
        var s = new ColorOverrideStore(new InMemoryConfigSection());
        s.Set("Sakura", "k", new(1, 1, 1));
        s.Clear("Sakura", "k");
        Assert.False(s.TryGet("Sakura", "k", out _));
    }

    [Fact]
    public void Overrides_PersistAcrossReload_AfterFlush()
    {
        var cfg = new InMemoryConfigSection();
        var s = new ColorOverrideStore(cfg);
        s.Set("Sakura", "k", new(0.2f, 0.4f, 0.6f, 1f));
        s.Flush();
        var reloaded = new ColorOverrideStore(cfg);
        Assert.True(reloaded.TryGet("Sakura", "k", out var v));
        Assert.True(System.Math.Abs(0.4f - v.G) < 0.005f);
    }

    [Fact]
    public void Set_WithoutFlush_DoesNotPersist()
    {
        var cfg = new InMemoryConfigSection();
        new ColorOverrideStore(cfg).Set("Sakura", "k", new(1, 0, 0)); // no Flush
        var reloaded = new ColorOverrideStore(cfg);
        Assert.False(reloaded.TryGet("Sakura", "k", out _));
    }

    [Fact]
    public void Sparse_OnlyStoresSetKeys()
    {
        var cfg = new InMemoryConfigSection();
        var s = new ColorOverrideStore(cfg);
        s.Set("Sakura", "k", new(1, 0, 0));
        Assert.False(s.Has("Sakura", "other"));
        Assert.True(s.Has("Sakura", "k"));
    }
}
