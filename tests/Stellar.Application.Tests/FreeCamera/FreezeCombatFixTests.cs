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
//  2. the drawn ROTATION was never held — PoseHold writes the press pose (position + rotation) and snaps to the logical
//     pose on release;
//  3. a killed monster vanished — DeferredRemovals queues EDisappearDead removals of frozen monsters and replays them, in
//     order, through the game's own RemoveEntity on unfreeze / scene clear; the gated prefix runs no chained callback
//     for a deferred call and each exactly once at the replay.
// (1. the ECS layer-speed gate, 4. the effect owner rule and 5. the fix-check verdict were removed 2026-10-02 late with the
// mechanisms they pinned: the freeze is a global time pause now — Time.timeScale = 0 stops animation, skills and effects,
// which no per-entity speed could; recon § Run 9.) Do not weaken.
public sealed class FreezeCombatFixTests
{
    private const long Monster = 7, Other = 8, Self = 42;

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

    // ---- helpers ----

    private static readonly MethodBase Method = typeof(FreezeCombatFixTests).GetMethod(nameof(Target), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void Target() { }

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
