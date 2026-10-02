using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Freeze review round 2 (2026-10-02, qa Important): the game POOLS models (RecycleModel*, RecycleSummonModel,
// RecyclePhotoModel), so the anim component a dead entity had can go to a respawn, a summon or a refused owner. The
// set_Speed gate kept the dead uuid's mapping: the new entity's init set_Speed(1.0) was substituted under the DEAD uuid,
// the new entity's speed read 0 so nothing kept a restore value, and it stayed at drawn speed 0 after unfreeze; a refused
// owner (the local player's own mount) got frozen. Fix: the AddEntity postfix re-keys the component (admitted → Track,
// refused → Forget) and the RemoveEntity prefix drops the leaving entity's component (O(1), uuid → component map). Also
// pinned here: the gate's main thread comes from the frame driver, and the always-on prefix entry counter. Do not weaken.
public sealed class FreezeRecycledCompTests
{
    private const int Main = 1;
    private const long Self = 42, Dead = 7, Respawn = 8, OwnMount = 99;
    private static readonly IntPtr Pooled = new(0x1000), Other = new(0x2000);

    [Fact]
    public void freeze_combat_resume_recycled_comp_follows_its_new_entity()
    {
        var (l, g) = Armed();
        g.Track(Pooled, Dead, FreezeKinds.Monster);           // stage 2: the dead entity's component
        l.AdmitSpeed(Dead, 1f);
        g.Rekey(Pooled, Respawn, FreezeKinds.Char, admitted: true);   // AddEntity postfix: the recycled component's new owner

        var init = 1f;                                        // the new entity's own init write
        Assert.True(g.TrySubstitute(Pooled, ref init, Main));
        Assert.Equal(0f, init);
        Assert.Equal(1f, l.Speeds[Respawn]);                  // kept under the NEW uuid: it resumes after unfreeze
        Assert.Equal(1, g.Held(DrawnSpeedGate.Bucket.Player));
        Assert.Equal(0, g.Held(DrawnSpeedGate.Bucket.Monster));
        Assert.Equal(1, g.Tracked);
        Assert.Equal(new[] { Respawn }, g.TrackedUuids);

        g.Untrack(Dead);                                      // the dead uuid no longer owns it: no effect
        Assert.Equal(1, g.Tracked);
    }

    [Fact]
    public void freeze_combat_resume_recycled_comp_refused_owner_is_never_frozen()
    {
        var (l, g) = Armed();
        g.Track(Pooled, Dead, FreezeKinds.Monster);
        // The local player re-appearing on a recycled model: AdmitAppeared refuses them by uuid — the ledger need not
        // exclude them — so the re-key must FORGET the component, not track it.
        g.Rekey(Pooled, 555, FreezeKinds.Char, admitted: false);
        var v = 1f;
        Assert.False(g.TrySubstitute(Pooled, ref v, Main));
        Assert.Equal(1f, v);
        Assert.Equal(0, g.Tracked);

        g.Track(Other, Dead, FreezeKinds.Monster);
        l.Exclude(OwnMount);                                  // the local player's own mount, refused as it appears
        g.Rekey(Other, OwnMount, FreezeKinds.Vehicle, admitted: false);
        var w = 1f;
        Assert.False(g.TrySubstitute(Other, ref w, Main));
        Assert.Equal(1f, w);
        Assert.Empty(g.TrackedUuids);
    }

    [Fact]
    public void freeze_combat_resume_recycled_comp_despawn_drops_the_mapping()
    {
        var (_, g) = Armed();
        g.Track(Pooled, Dead, FreezeKinds.Monster);
        g.Untrack(Dead);                                      // RemoveEntity prefix
        var v = 1f;                                           // the model, recycled before any AddEntity re-key
        Assert.False(g.TrySubstitute(Pooled, ref v, Main));
        Assert.Equal(1f, v);
        Assert.Equal(0, g.Tracked);

        g.Track(Pooled, Respawn, FreezeKinds.Char);           // a model swap: the entity's previous component is dropped
        g.Track(Other, Respawn, FreezeKinds.Char);
        Assert.Equal(1, g.Tracked);
        var old = 1f;
        Assert.False(g.TrySubstitute(Pooled, ref old, Main));
        Assert.Equal(1f, old);
    }

    [Fact]
    public void freeze_gate_main_thread_comes_from_the_frame_driver()
    {
        var l = new FreezeLedger();
        l.Begin(Self);
        var g = new DrawnSpeedGate();
        g.Arm(l);                                             // whoever called FreezeAll: no thread taken from it
        g.Track(Pooled, Dead, FreezeKinds.Monster);
        var v = 1f;
        Assert.False(g.TrySubstitute(Pooled, ref v, Main));   // main thread not observed yet: passthrough
        Assert.Equal(1f, v);
        g.ObserveMainThread(Main);                            // the late frame
        Assert.True(g.TrySubstitute(Pooled, ref v, Main));
        Assert.False(g.TrySubstitute(Pooled, ref v, threadId: 9));
    }

    [Fact]
    public void freeze_gate_counts_every_prefix_entry_per_freeze()
    {
        var (l, g) = Armed();
        g.CountCall();
        g.CountCall();
        g.Disarm();
        g.CountCall();                                        // unarmed calls count too: the real set_Speed rate
        Assert.Equal(3, g.Calls);
        g.Arm(l);
        Assert.Equal(0, g.Calls);
    }

    private static (FreezeLedger, DrawnSpeedGate) Armed()
    {
        var l = new FreezeLedger();
        l.Begin(Self);
        var g = new DrawnSpeedGate();
        g.Arm(l);
        g.ObserveMainThread(Main);
        return (l, g);
    }
}
