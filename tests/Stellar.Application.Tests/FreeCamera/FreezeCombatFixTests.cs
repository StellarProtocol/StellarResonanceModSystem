using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Hooks;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Owner report 2026-10-02, MAIN client, boss fight with the diagnostics build (combat-diag-main-log-20261002.log,
// report .superpowers/sdd/posing/combat-freeze-fix-report.md): "boss suppose to stay in the air but it's still animated
// down and be able to move … boss can rotate, and animated". Root causes pinned here, each through its pure rule:
//  1. the ECS animator's per-LAYER speed (SetAnimatorLayerData / PlayState / PlayClip / PlayDynamicState) restarted a
//     skill layer past both gated speeds — EcsSpeedGate holds it at 0 and replays the game's wishes on release;
//  2. the drawn ROTATION was never held — PoseHold writes the press pose (position + rotation) and snaps to the logical
//     pose on release;
//  3. a killed monster vanished — DeferredRemovals queues EDisappearDead removals of frozen monsters and replays them, in
//     order, through the game's own RemoveEntity on unfreeze / scene clear; the gated prefix runs no chained callback
//     for a deferred call and each exactly once at the replay;
//  4. effects initialised outside AddEffectDisplay — FreezeEffectRule freezes every new effect while frozen (owner
//     ignored: scene-stays spec § 3 keeps effects global).
// Do not weaken.
public sealed class FreezeCombatFixTests
{
    private const int Main = 1;
    private const long Monster = 7, Other = 8, Self = 42;
    private const uint Uid = 500, Uid2 = 501;

    // ---- 1. ECS layer speed hold and restore ----

    [Fact]
    public void freeze_combat_ecs_layer_write_is_held_at_zero_and_its_wish_replayed_on_release()
    {
        var g = ArmedEcs();
        Assert.True(g.Track(Uid, Monster));                     // stage 2: first tracking → the caller writes the model to 0
        Assert.False(g.Track(Uid, Monster));                    // once only
        var speed = 1.2f;
        Assert.True(g.TrySubstitute(Uid, 1, ref speed, 1f, Main));   // a skill stage restarts the upper layer
        Assert.Equal(0f, speed);
        Assert.Equal(1, g.Held);
        Assert.Equal(1, g.HeldFor(Monster));
        g.Disarm();
        var plan = g.TakeReleasePlan(Uid, controllerSpeed: 0.9f);
        Assert.Equal(new[] { new EcsSpeedGate.LayerWrite(-1, 0.9f, 1f), new EcsSpeedGate.LayerWrite(1, 1.2f, 1f) }, plan);
        Assert.Single(g.TakeReleasePlan(Uid, 0.9f));             // taken: never replayed twice
    }

    [Fact]
    public void freeze_combat_ecs_untracked_own_offthread_and_unarmed_writes_pass_untouched()
    {
        var g = ArmedEcs();
        g.Track(Uid, Monster);
        var v = 1f;
        Assert.False(g.TrySubstitute(Uid2, 0, ref v, 1f, Main));     // the local player / a photo copy / a UI model
        Assert.Equal(1f, v);
        g.OwnWrite = true;
        Assert.False(g.TrySubstitute(Uid, 0, ref v, 1f, Main));      // our own write
        g.OwnWrite = false;
        Assert.Equal(1f, v);
        Assert.False(g.TrySubstitute(Uid, 0, ref v, 1f, threadId: 2));   // off the main thread: passes, counted as a leak
        Assert.Equal(1f, v);
        Assert.Equal(1, g.Leaked);
        Assert.Equal(1, g.LeakedFor(Monster));
        g.Disarm();
        Assert.False(g.TrySubstitute(Uid, 0, ref v, 1f, Main));
        Assert.Equal(1f, v);
        Assert.Equal(0, g.Held);
    }

    [Fact]
    public void freeze_combat_ecs_release_plan_keeps_each_layers_latest_wish_after_the_last_whole_model_write()
    {
        var g = ArmedEcs();
        g.Track(Uid, Monster);
        Write(g, 1, 1.0f);
        Write(g, 2, 0.5f);
        Write(g, 1, 1.3f);
        Write(g, -1, 0.8f);   // the controller's whole-model write overrides every layer before it
        Write(g, 2, 0.7f);
        Write(g, 0, 1.1f);
        var plan = g.TakeReleasePlan(Uid, 1f);
        Assert.Equal(new[] { (-1, 1f), (2, 0.7f), (0, 1.1f) }, plan.Select(w => (w.Layer, w.Speed)));
    }

    [Fact]
    public void freeze_combat_ecs_despawned_or_recycled_uid_is_never_replayed()
    {
        var g = ArmedEcs();
        g.Track(Uid, Monster);
        Write(g, 1, 1.2f);
        Assert.Equal(Uid, g.Untrack(Monster));                   // RemoveEntity: the uid may be recycled
        Assert.False(g.Tracks(Uid));
        Assert.Single(g.TakeReleasePlan(Uid, 1f));               // its wish is gone

        g.Track(Uid, Monster);
        Write(g, 1, 1.2f);
        Assert.True(g.Track(Uid, Other));                        // a pooled uid follows its NEW owner
        Assert.Equal(Other, g.Snapshot().Single().Uuid);
        Assert.Single(g.TakeReleasePlan(Uid, 1f));               // the dead owner's wish is not replayed onto it
        Assert.Equal(0u, g.UidOf(Monster));
    }

    // ---- 2. rotation hold and restore ----

    [Fact]
    public void freeze_combat_rotation_hold_writes_the_press_pose_every_tick()
    {
        var hold = new PoseHold<P, R>();
        hold.Add(Monster, new P(1), new R(10));
        hold.Add(Other, new P(2), null);                          // rotation unreadable at hold time: never written
        var comps = new Dictionary<long, object> { [Monster] = "m", [Other] = "o" };
        var writes = new List<string>();
        for (var tick = 0; tick < 2; tick++)                      // the game rotates in between: the hold writes it back
            hold.Tick(u => comps.TryGetValue(u, out var c) ? c : null, (c, p) => writes.Add($"{c}:p{p.V}"), (c, r) => writes.Add($"{c}:r{r.V}"));
        Assert.Equal(new[] { "m:p1", "m:r10", "o:p2", "m:p1", "m:r10", "o:p2" }, writes);

        comps.Remove(Monster);                                   // gone (despawning): skipped, the rest still written
        writes.Clear();
        hold.Tick(u => comps.TryGetValue(u, out var c) ? c : null, (c, p) => writes.Add($"{c}:p{p.V}"), (c, r) => writes.Add($"{c}:r{r.V}"));
        Assert.Equal(new[] { "o:p2" }, writes);
    }

    [Fact]
    public void freeze_combat_rotation_release_snaps_to_the_logical_pose()
    {
        var hold = new PoseHold<P, R>();
        hold.Add(Monster, new P(1), new R(10));
        hold.Add(Other, new P(2), new R(20));
        hold.Add(9, new P(3), new R(30));
        var writes = new List<string>();
        var errors = 0;
        hold.Release(u => u switch
        {
            Monster => ("m", new P(100), new R(110)),               // logical pose: position AND rotation
            Other => ("o", new P(200), (R?)null),                    // no logical rotation: the drawn one is left to the game
            _ => throw new InvalidOperationException(),             // one failing entity never skips the others
        }, (c, p) => writes.Add($"{c}:p{p.V}"), (c, r) => writes.Add($"{c}:r{r.V}"), _ => errors++);
        Assert.Equal(new[] { "m:p100", "m:r110", "o:p200" }, writes);
        Assert.Equal(1, errors);
        Assert.True(hold.Remove(Monster));
        Assert.False(hold.Contains(Monster));
        Assert.Equal(new R(20), hold.Find(Other)!.Value.Rot);
    }

    // ---- 3. deferred removal of a monster killed while frozen ----

    [Fact]
    public void freeze_combat_killed_monster_removal_is_deferred_and_replayed_in_order()
    {
        var d = Armed();
        foreach (var u in new long[] { 3, 1, 2 }) Assert.Equal(DeferredRemovals.Decision.Defer, d.Decide(Dead(u), Args(u)));
        Assert.Equal(3, d.Count);
        Assert.Equal(DeferredRemovals.Decision.Defer, d.Decide(Dead(1), Args(1)));   // the same death again: no second entry
        Assert.Equal(3, d.Count);

        var replayed = new List<(long, bool, object?)>();
        Assert.Equal(3, d.Replay(Same, (u, a) => replayed.Add((u, d.Replaying, a[0]))));    // unfreeze / scene clear
        Assert.Equal(new[] { (3L, true, (object?)3L), (1L, true, 1L), (2L, true, 2L) }, replayed);
        Assert.Equal(0, d.Count);
        Assert.False(d.Replaying);
        Assert.Equal(DeferredRemovals.Decision.Defer, d.Decide(Dead(4), Args(4)));   // still armed after a scene-clear replay
        Assert.Equal(1, d.Replay(Same, (_, _) => { }));
        d.Disarm();
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(5), Args(5)));      // not frozen: never deferred
        Assert.Equal(0, d.Count);
    }

    [Fact]
    public void freeze_combat_only_a_frozen_monsters_dead_removal_is_deferred()
    {
        var d = Armed();
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(1) with { Type = "EDisappearNormal" }, Args(1)));    // left AOI
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(1) with { Type = "EDisappearDestroy" }, Args(1)));   // summon expiry
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(1) with { Type = "EDisappearTransferLeave" }, Args(1)));
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(1) with { Kind = FreezeKinds.ClientBullet }, Args(1)));
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(1) with { Kind = FreezeKinds.Char }, Args(1)));
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(1) with { Immediate = true }, Args(1)));
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(Self) with { Excluded = true }, Args(Self)));        // never self
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(0), Args(0)));
        Assert.Equal(0, d.Count);
        d.Replay(Same, (_, _) => Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(9), Args(9))));   // replaying: always runs
    }

    [Fact]
    public void freeze_combat_a_reused_uuid_supersedes_the_queued_removal_and_the_queue_is_bounded()
    {
        var d = Armed();
        Assert.Equal(DeferredRemovals.Decision.Defer, d.Decide(Dead(1), Args(1)));
        // ZEntityCreator.TryCreateEntityReady re-creating uuid 1 calls RemoveEntity(1, …, removeImmediately: true).
        Assert.Equal(DeferredRemovals.Decision.RunSuperseding, d.Decide(Dead(1) with { Immediate = true }, Args(1)));
        Assert.False(d.IsQueued(1));
        Assert.Equal(0, d.Replay(Same, (_, _) => throw new InvalidOperationException("must not replay a superseded removal")));
        for (long u = 1; u <= DeferredRemovals.Cap; u++) Assert.Equal(DeferredRemovals.Decision.Defer, d.Decide(Dead(u), Args(u)));
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(1000), Args(1000)));   // over the cap: the game removes it
        Assert.Equal(DeferredRemovals.Cap, d.Drop());
        Assert.Equal(0, d.Count);
    }

    [Fact]
    public void freeze_combat_deferred_removal_runs_each_chained_callback_exactly_once_in_order()
    {
        var gates = new Dictionary<MethodBase, Func<object?, object?[], bool>>();
        var callbacks = new Dictionary<MethodBase, Action<object?, object?[]>>();
        var calls = new List<string>();
        var d = Armed();
        HookCallbackTable.Add(callbacks, Method, (_, a) => calls.Add("posing:" + a[0]));    // posing's despawn
        HookCallbackTable.Add(callbacks, Method, (_, a) => calls.Add("freeze:" + a[0]));    // the freeze's leave
        Assert.True(HookCallbackTable.PrefixPatched(callbacks, gates, Method));
        Assert.True(HookCallbackTable.AddGate(gates, Method, (_, a) =>
            d.Decide(new DeferredRemovals.Call((long)a[0]!, a[1] as string, a[2] is true, FreezeKinds.Monster, false, IdentityOf((long)a[0]!), false), a)
            != DeferredRemovals.Decision.Defer));
        var originals = 0;
        void Call(object?[] a) { if (HookCallbackTable.RunPrefix(gates, callbacks, Method, null, a)) originals++; }

        Call(Args(Monster));                                     // the kill while frozen
        Assert.Empty(calls);                                     // no callback, no game body
        Assert.Equal(0, originals);
        d.Replay(Same, (_, a) => Call(a));                             // unfreeze: through the same trampoline
        Assert.Equal(new[] { "posing:7", "freeze:7" }, calls);
        Assert.Equal(1, originals);
        Call(new object?[] { Other, "EDisappearNormal", false });   // a plain removal: callbacks once, game body once
        Assert.Equal(new[] { "posing:7", "freeze:7", "posing:8", "freeze:8" }, calls);
        Assert.Equal(2, originals);
    }

    [Fact]
    public void freeze_combat_hooker_gates_chain_with_and_and_a_throwing_gate_fails_open()
    {
        var gates = new Dictionary<MethodBase, Func<object?, object?[], bool>>();
        var callbacks = new Dictionary<MethodBase, Action<object?, object?[]>>();
        Assert.False(HookCallbackTable.PrefixPatched(callbacks, gates, Method));
        Assert.True(HookCallbackTable.AddGate(gates, Method, (_, _) => true));
        Assert.True(HookCallbackTable.PrefixPatched(callbacks, gates, Method));     // a gate alone already patched it
        Assert.False(HookCallbackTable.AddGate(gates, Method, (_, a) => a[0] is not "veto"));
        Assert.True(HookCallbackTable.RunPrefix(gates, callbacks, Method, null, new object?[] { "go" }));
        Assert.False(HookCallbackTable.RunPrefix(gates, callbacks, Method, null, new object?[] { "veto" }));
        var broken = new Dictionary<MethodBase, Func<object?, object?[], bool>> { [Method] = (_, _) => throw new InvalidOperationException() };
        Assert.True(HookCallbackTable.RunPrefix(broken, callbacks, Method, null, Array.Empty<object?>()));
    }

    // ---- 4. the effect owner rule ----

    [Fact]
    public void freeze_combat_effect_owner_rule_freezes_every_new_effect_while_frozen()
    {
        Assert.True(FreezeEffectRule.ShouldFreeze(frozen: true, alreadyTouched: false, owner: Monster, self: Self));   // the boss's skill fx
        Assert.True(FreezeEffectRule.ShouldFreeze(true, false, owner: Self, self: Self));     // scene-stays § 3: yours too
        Assert.True(FreezeEffectRule.ShouldFreeze(true, false, owner: 0, self: Self));        // unowned
        Assert.False(FreezeEffectRule.ShouldFreeze(true, alreadyTouched: true, Monster, Self));   // already in the ledger
        Assert.False(FreezeEffectRule.ShouldFreeze(frozen: false, false, Monster, Self));
    }

    // ---- 5. the fix-check diagnostics verdict ----

    [Fact]
    public void freeze_combat_fix_check_entity_verdict_names_what_moved()
    {
        Assert.Equal("HELD", FreezeDiagVerdict.EntityVerdict(0.01f, 1f, ecsModel: true, ecsUntracked: false, ecsLeaked: 0));
        Assert.Equal("OFF:pos", FreezeDiagVerdict.EntityVerdict(0.2f, 0f, true, false, 0));
        Assert.Equal("OFF:rot", FreezeDiagVerdict.EntityVerdict(0f, 5f, true, false, 0));
        Assert.Equal("OFF:ecs", FreezeDiagVerdict.EntityVerdict(0f, 0f, true, true, 0));
        Assert.Equal("OFF:ecs", FreezeDiagVerdict.EntityVerdict(0f, 0f, true, false, 3));
        Assert.Equal("HELD", FreezeDiagVerdict.EntityVerdict(0f, 0f, ecsModel: false, ecsUntracked: true, ecsLeaked: 0));   // GameObject model
        Assert.Equal("OFF:pos,rot,ecs", FreezeDiagVerdict.EntityVerdict(1f, 90f, true, true, 1));

        Assert.Equal(0f, FreezeDiagVerdict.AngleDegrees((0, 0, 0, 1), (0, 0, 0, 1)), 2);
        Assert.Equal(90f, FreezeDiagVerdict.AngleDegrees((0, 0, 0, 1), (0, 0.70710677f, 0, 0.70710677f)), 2);
        Assert.Equal(0f, FreezeDiagVerdict.AngleDegrees((0, 0.6f, 0, 0.8f), (0, -0.6f, 0, -0.8f)), 2);   // q and −q: same rotation
        Assert.True(float.IsNaN(FreezeDiagVerdict.AngleDegrees((0, 0, 0, 0), (0, 0, 0, 1))));

        var tags = FreezeDiagVerdict.Explain(new FreezeDiagTally { Monsters = 2, RotOffHold = 1, EcsUntracked = 1, EcsLeaked = 1 });
        Assert.Equal(new[] { "ROTATION-OFF-HOLD", "ECS-UNTRACKED", "ECS-SPEED-LEAK" }, tags);
    }

    // ---- helpers ----

    private static readonly MethodBase Method = typeof(FreezeCombatFixTests).GetMethod(nameof(Target), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void Target() { }

    private static EcsSpeedGate ArmedEcs()
    {
        var g = new EcsSpeedGate();
        g.ObserveMainThread(Main);
        g.Arm();
        return g;
    }

    private static void Write(EcsSpeedGate g, int layer, float speed)
    {
        var s = speed;
        Assert.True(g.TrySubstitute(Uid, layer, ref s, 1f, Main));
        Assert.Equal(0f, s);
    }

    private static DeferredRemovals Armed()
    {
        var d = new DeferredRemovals();
        d.Arm();
        return d;
    }

    private static DeferredRemovals.Call Dead(long uuid) =>
        new(uuid, DeferredRemovals.DeadType, Immediate: false, Kind: FreezeKinds.Monster, Excluded: false, Identity: IdentityOf(uuid),
            OffMainThread: false);

    /// <summary>The native pointer GetEntity(uuid) serves (a fixed fake per uuid).</summary>
    internal static nint IdentityOf(long uuid) => (nint)(0x10000 + uuid);

    /// <summary>GetEntity(uuid) still serves the entity kept at defer time.</summary>
    private static nint Same(long uuid) => IdentityOf(uuid);

    private static object?[] Args(long uuid) => new object?[] { uuid, DeferredRemovals.DeadType, false };

    private readonly record struct P(int V);

    private readonly record struct R(int V);
}
