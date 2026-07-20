using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Pins the wire-position cache: set/get, the staleness bound, cap eviction, scene clear,
/// AOI-disappear removal, and the yaw-only / position-merge semantics. Clock is injected so
/// staleness is deterministic (no wall-clock dependence).
/// </summary>
public sealed class WireEntityPositionsTests
{
    private static WirePos Pos(float x, float y, float z, float dir = 0f, bool hasDir = false)
        => new(x, y, z, dir, hasDir);

    [Fact]
    public void SetThenGet_WithinStaleness_ReturnsSample()
    {
        long now = 1_000;
        var cache = new WireEntityPositions(() => now);
        cache.OnPosition(42, Pos(170f, 100f, -290f));

        var ok = cache.TryGetFresh(42, maxStaleMs: 5_000, out var s);

        Assert.True(ok);
        Assert.Equal(170f, s.X, 3);
        Assert.Equal(100f, s.Y, 3);
        Assert.Equal(-290f, s.Z, 3);
        Assert.Equal(0L, s.AgeMs);
    }

    [Fact]
    public void Get_PastStaleness_ReturnsFalse()
    {
        long now = 1_000;
        var cache = new WireEntityPositions(() => now);
        cache.OnPosition(42, Pos(1f, 2f, 3f));

        now = 1_000 + 5_001; // one ms past the 5s bound
        Assert.False(cache.TryGetFresh(42, maxStaleMs: 5_000, out _));
    }

    [Fact]
    public void Get_AtStalenessBoundary_ReturnsTrue()
    {
        long now = 0;
        var cache = new WireEntityPositions(() => now);
        cache.OnPosition(7, Pos(1f, 2f, 3f));

        now = 5_000; // exactly the bound
        Assert.True(cache.TryGetFresh(7, maxStaleMs: 5_000, out var s));
        Assert.Equal(5_000L, s.AgeMs);
    }

    [Fact]
    public void Get_Absent_ReturnsFalse()
    {
        var cache = new WireEntityPositions(() => 0);
        Assert.False(cache.TryGetFresh(999, 5_000, out _));
    }

    [Fact]
    public void DirOnly_HasNoPosition_NotServed()
    {
        var cache = new WireEntityPositions(() => 0);
        cache.OnDir(5, 1.5f);
        Assert.False(cache.TryGetFresh(5, 5_000, out _)); // yaw-only entry has no real position
    }

    [Fact]
    public void DirOnly_DoesNotExtendPositionFreshness()
    {
        // Regression pin (run sea/UaU5VejCA0, thanatos walk-in): a facing-only AttrDir(50) delta must
        // NOT re-freshen a stale position. Pessimistic in-window case — AttrPos stops but AttrDir keeps
        // ticking — must let the position age out at 5s, not freeze the player at their last cached spot.
        long now = 0;
        var cache = new WireEntityPositions(() => now);
        cache.OnPosition(42, Pos(170f, 100f, -290f)); // stamped at t=0

        now = 4_900;
        cache.OnDir(42, 1.2f); // facing update inside the window — must not advance position freshness

        now = 5_001; // one ms past the bound relative to the ORIGINAL position stamp
        Assert.False(cache.TryGetFresh(42, maxStaleMs: 5_000, out _));
    }

    [Fact]
    public void DirThenPosition_MergesYaw()
    {
        var cache = new WireEntityPositions(() => 0);
        cache.OnDir(5, 42f);
        cache.OnPosition(5, Pos(1f, 2f, 3f)); // position without its own dir keeps the AttrDir yaw

        Assert.True(cache.TryGetFresh(5, 5_000, out var s));
        Assert.True(s.HasYaw);
        Assert.Equal(42f, s.Yaw, 3);
    }

    [Fact]
    public void PositionWithOwnDir_OverridesStoredYaw()
    {
        var cache = new WireEntityPositions(() => 0);
        cache.OnDir(5, 42f);
        cache.OnPosition(5, Pos(1f, 2f, 3f, dir: 99f, hasDir: true));

        Assert.True(cache.TryGetFresh(5, 5_000, out var s));
        Assert.Equal(99f, s.Yaw, 3);
    }

    [Fact]
    public void Clear_DropsAll()
    {
        var cache = new WireEntityPositions(() => 0);
        cache.OnPosition(1, Pos(1f, 1f, 1f));
        cache.OnPosition(2, Pos(2f, 2f, 2f));
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGetFresh(1, 5_000, out _));
    }

    [Fact]
    public void Remove_DropsOne()
    {
        var cache = new WireEntityPositions(() => 0);
        cache.OnPosition(1, Pos(1f, 1f, 1f));
        cache.OnPosition(2, Pos(2f, 2f, 2f));
        cache.Remove(1);
        Assert.False(cache.TryGetFresh(1, 5_000, out _));
        Assert.True(cache.TryGetFresh(2, 5_000, out _));
    }

    [Fact]
    public void CapEviction_BoundsSizeAndEvictsOldest()
    {
        long tick = 0;
        var cache = new WireEntityPositions(() => tick);

        // Insert one past the cap; each insert advances the clock so eviction picks the oldest.
        for (long i = 0; i < WireEntityPositions.MaxEntities + 1; i++)
        {
            tick = i;
            cache.OnPosition(i, Pos(1f, 1f, 1f));
        }

        Assert.True(cache.Count <= WireEntityPositions.MaxEntities);
        // uuid 0 was the oldest (stamp 0) → evicted; the newest is present.
        Assert.False(cache.TryGetFresh(0, long.MaxValue, out _));
        Assert.True(cache.TryGetFresh(WireEntityPositions.MaxEntities, long.MaxValue, out _));
    }
}
