using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Scene-stays spec § 3 (regression scene-stays-self-never-frozen), RE-PINNED 2026-10-02 (late) for the global time pause:
// the spec amendment pauses EVERYONE, the local player included ("your own character is never frozen" is superseded — the
// owner chose the whole-world pause), so the ledger no longer keeps animation factors or drawn speeds. What stays pinned:
// the local player (and their own mount) is dropped from the press's entity list and refused by every admission, so the
// position hold never pins them and their removal is never deferred.
public sealed class FreezeLedgerTests
{
    [Fact]
    public void scene_stays_self_never_held()
    {
        var l = new FreezeLedger();
        l.Begin(self: 42);
        var ids = new List<long> { 7, 42, 9, 42 };
        l.WithoutSelf(ids);
        Assert.Equal(new long[] { 7, 9 }, ids);
        Assert.True(l.Excludes(42));
        Assert.False(l.Excludes(7));
        Assert.False(FreezeTargets.MayHold(l, 42, FreezeKinds.Char, 0f, alreadyHeld: false));
        Assert.True(FreezeTargets.MayHold(l, 7, FreezeKinds.Char, 0f, alreadyHeld: false));
        Assert.True(l.Exclude(99));                                          // the mount they ride
        Assert.False(l.Exclude(99));
        Assert.True(l.Excludes(99));
        l.Clear();
        Assert.Equal(0, l.Self);
        Assert.False(l.Excludes(42));
        Assert.False(l.Excludes(99));
    }

    [Fact]
    public void An_unknown_local_player_excludes_nobody()
    {
        var l = new FreezeLedger();
        l.Begin(self: 0);
        var ids = new List<long> { 0, 7 };
        l.WithoutSelf(ids);
        Assert.Equal(new long[] { 0, 7 }, ids);
        Assert.False(l.Exclude(0));
        Assert.Empty(l.Excluded);
    }
}
