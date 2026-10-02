using System;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Owner report 2026-10-02 (MAIN, boss in combat): with the scene freeze on, monsters freeze for a few seconds and then move
// again. Recon run 7: FreezeLedger refused a second save, so every re-apply (the appear re-check) wrote nothing after the
// first; the game's factor writers are inlined, so the fix keeps the DRAWN speed at 0 through a prefix on
// AnimCompBase.set_Speed (DrawnSpeedGate). Pinned: a re-apply after a game rewrite writes 0 again and the unfreeze
// restores the speed the game last asked for; the gate substitutes only for tracked, non-excluded entities while armed;
// anything else passes through untouched. Do not weaken.
public sealed class FreezeCombatResumeTests
{
    private const int Main = 1;
    private static readonly IntPtr MonsterComp = new(0x1000), SelfComp = new(0x2000), OtherComp = new(0x3000);

    [Fact]
    public void freeze_combat_resume_reapply_writes_zero_again_and_restores_the_latest_game_speed()
    {
        var l = new FreezeLedger();
        l.Begin(self: 42);
        Assert.True(l.AdmitSpeed(7, 1f));       // stage 2: write 0, keep 1
        Assert.True(l.AdmitSpeed(7, 1.3f));     // the game restarted it at 1.3: write 0 AGAIN (was a no-op before the fix)
        Assert.Equal(1.3f, l.Speeds[7]);        // the latest game speed is the restore value
        Assert.Equal(1.3f, FreezeLedger.RestoreSpeed(l.Speeds[7], current: 0f));
        Assert.False(l.AdmitSpeed(42, 1f));     // never the local player
        Assert.DoesNotContain(42L, l.Speeds.Keys);
    }

    [Fact]
    public void freeze_combat_resume_reapply_keeps_the_first_factor_prior()
    {
        var l = new FreezeLedger();
        l.Begin(self: 42);
        Assert.True(l.SaveFactor(7, 1f));
        Assert.False(l.SaveFactor(7, 0f));      // a re-apply never re-saves: the factor keeps its first prior
        Assert.Equal(1f, FreezeLedger.RestoreValue(l.Factors[7], current: 0f));
    }

    [Fact]
    public void freeze_combat_resume_gate_substitutes_only_for_frozen_non_excluded_entities()
    {
        var (l, g) = Armed();
        g.Track(MonsterComp, 7, FreezeKinds.Monster);
        g.Track(SelfComp, 42, FreezeKinds.Char);   // the local player: never tracked

        var v = 1.2f;
        Assert.True(g.TrySubstitute(MonsterComp, ref v, Main));
        Assert.Equal(0f, v);
        Assert.Equal(1.2f, l.Speeds[7]);
        Assert.Equal(1, g.Held(DrawnSpeedGate.Bucket.Monster));

        var self = 1f;
        Assert.False(g.TrySubstitute(SelfComp, ref self, Main));
        Assert.Equal(1f, self);
        var other = 1f;
        Assert.False(g.TrySubstitute(OtherComp, ref other, Main));   // not part of this freeze
        Assert.Equal(1f, other);
        Assert.Equal(1, g.Tracked);
    }

    [Fact]
    public void freeze_combat_resume_unfrozen_is_passthrough()
    {
        var g = new DrawnSpeedGate();
        var v = 1f;
        Assert.False(g.TrySubstitute(MonsterComp, ref v, Main));     // never armed
        Assert.Equal(1f, v);

        var (_, armed) = Armed();
        armed.Track(MonsterComp, 7, FreezeKinds.Monster);
        armed.OwnWrite = true;                                      // the freeze's own write (stage 2 / restore)
        Assert.False(armed.TrySubstitute(MonsterComp, ref v, Main));
        armed.OwnWrite = false;
        Assert.False(armed.TrySubstitute(MonsterComp, ref v, threadId: 9));   // off the main thread
        armed.Disarm();                                             // unfreeze: before any restore write
        Assert.False(armed.TrySubstitute(MonsterComp, ref v, Main));
        Assert.Equal(1f, v);
        Assert.False(armed.Armed);
    }

    [Fact]
    public void freeze_combat_resume_game_zero_never_replaces_the_restore_speed()
    {
        var (l, g) = Armed();
        l.AdmitSpeed(7, 1f);
        g.Track(MonsterComp, 7, FreezeKinds.Monster);
        var v = 0f;                                                 // e.g. the game applying OUR frozen factor
        Assert.True(g.TrySubstitute(MonsterComp, ref v, Main));
        Assert.Equal(1f, l.Speeds[7]);                              // it must still resume at 1 after unfreeze
        Assert.Equal(0, g.Held(DrawnSpeedGate.Bucket.Monster));     // nothing was moving: not counted
    }

    [Fact]
    public void freeze_combat_resume_gate_lets_a_late_excluded_entity_go()
    {
        var (l, g) = Armed();
        g.Track(MonsterComp, 99, FreezeKinds.Vehicle);
        l.Exclude(99);                                              // turned out to be the local player's mount
        var v = 1f;
        Assert.False(g.TrySubstitute(MonsterComp, ref v, Main));
        Assert.Equal(1f, v);
        g.Untrack(99);
        Assert.Equal(0, g.Tracked);
    }

    [Fact]
    public void freeze_combat_resume_rearm_forgets_the_last_freeze()
    {
        var (l, g) = Armed();
        g.Track(MonsterComp, 7, FreezeKinds.Monster);
        var v = 1f;
        g.TrySubstitute(MonsterComp, ref v, Main);
        g.Disarm();
        Assert.Equal(1, g.Held(DrawnSpeedGate.Bucket.Monster));     // kept for the summary line after disarm
        g.Arm(l, Main);
        Assert.Equal(0, g.Held(DrawnSpeedGate.Bucket.Monster));
        Assert.Equal(0, g.Tracked);
    }

    [Theory]
    [InlineData(FreezeKinds.Monster, (int)DrawnSpeedGate.Bucket.Monster)]
    [InlineData(FreezeKinds.Char, (int)DrawnSpeedGate.Bucket.Player)]
    [InlineData(FreezeKinds.Npc, (int)DrawnSpeedGate.Bucket.Npc)]
    [InlineData(FreezeKinds.Pet, (int)DrawnSpeedGate.Bucket.Pet)]
    [InlineData(FreezeKinds.VanityPet, (int)DrawnSpeedGate.Bucket.Pet)]
    [InlineData(FreezeKinds.Vehicle, (int)DrawnSpeedGate.Bucket.Mount)]
    [InlineData(FreezeKinds.Dummy, (int)DrawnSpeedGate.Bucket.Other)]
    public void Summary_buckets_follow_the_entity_kind(int kind, int bucket) =>
        Assert.Equal((DrawnSpeedGate.Bucket)bucket, DrawnSpeedGate.BucketOf(kind));

    private static (FreezeLedger, DrawnSpeedGate) Armed()
    {
        var l = new FreezeLedger();
        l.Begin(self: 42);
        var g = new DrawnSpeedGate();
        g.Arm(l, Main);
        return (l, g);
    }
}
