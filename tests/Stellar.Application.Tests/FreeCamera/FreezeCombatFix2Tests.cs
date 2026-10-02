using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Review findings on the combat-freeze fix (03bcdc5, report .superpowers/sdd/posing/combat-freeze-fix2-report.md), each
// pinned through its pure rule:
//  1. an effect frozen through its own instance (ZEffect.Init — never in EffectDict) stayed frozen: the manager's
//     SetEffectFreeze(uid, false) cannot see it. FreezeEffectRule.Unfreeze routes a uid missing from EffectDict through the
//     kept instance, only when it still carries the same uid and is not being destroyed;
//  2. a deferred removal replayed by uuid alone could remove a DIFFERENT entity the uuid was reused for: the replay now
//     requires GetEntity(uuid) to serve the same native pointer kept at defer time;
//  4. a throwing flush could skip the teardown (stuck-frozen scene): FreezeTeardown.RunAfterFlush;
//  7. a leaving entity kept its ECS layers at 0 (a pooled model reused frozen): EcsSpeedGate.Release writes it back first;
//  8. the ECS prefixes bind each writer only to its exact layer type;
//  9. the removal gate passes through off the main thread.
// Do not weaken.
public sealed class FreezeCombatFix2Tests
{
    private const int Main = 1;
    private const long Monster = 7, Other = 8;
    private const uint Uid = 500, Uid2 = 501;

    // ---- 1. Init-frozen effects are unfrozen through their instance ----

    [Fact]
    public void freeze_combat_init_frozen_effect_missing_from_effect_dict_is_unfrozen_through_its_instance()
    {
        var instances = new Dictionary<long, (long, bool)>
        {
            [2] = (2, false),     // Init-frozen, never added to EffectDict, still the same effect
            [3] = (99, false),    // pooled and recycled for another effect meanwhile: never touched
            [4] = (4, true),      // being destroyed: never touched
        };
        var byManager = new List<long>();
        var byInstance = new List<long>();
        var counts = FreezeEffectRule.Unfreeze(new long[] { 1, 2, 3, 4, 5 }, new HashSet<long> { 1 },
            uid => uid == 1 ? throw new InvalidOperationException("an EffectDict uid never reads the instance")
                : instances.TryGetValue(uid, out var fx) ? fx : null,
            byManager.Add, byInstance.Add);
        Assert.Equal(new long[] { 1 }, byManager);
        Assert.Equal(new long[] { 2 }, byInstance);
        Assert.Equal(new FreezeEffectRule.UnfreezeCounts(Manager: 1, Instance: 1, Stale: 2, Lost: 1), counts);
        Assert.Equal(4, counts.MissingFromDict);   // the diagnostics count: ledger uids missing from EffectDict
    }

    [Fact]
    public void freeze_combat_effect_release_falls_back_to_the_manager_and_one_failure_never_skips_the_rest()
    {
        var byManager = new List<long>();
        var all = FreezeEffectRule.Unfreeze(new long[] { 1, 2 }, inDict: null, _ => throw new InvalidOperationException(),
            byManager.Add, _ => throw new InvalidOperationException());
        Assert.Equal(new long[] { 1, 2 }, byManager);                 // EffectDict not listable: the manager, as before
        Assert.Equal(2, all.Manager);

        var byInstance = new List<long>();
        var counts = FreezeEffectRule.Unfreeze(new long[] { 1, 2 }, new HashSet<long>(), uid => (uid, false), _ => { },
            uid => { if (uid == 1) throw new InvalidOperationException(); byInstance.Add(uid); });
        Assert.Equal(new long[] { 2 }, byInstance);
        Assert.Equal(1, counts.Stale);

        Assert.Equal(FreezeEffectRule.UnfreezeRoute.Manager, FreezeEffectRule.Route(true, (9, true), 1));
        Assert.Equal(FreezeEffectRule.UnfreezeRoute.Instance, FreezeEffectRule.Route(false, (1, false), 1));
        Assert.Equal(FreezeEffectRule.UnfreezeRoute.Stale, FreezeEffectRule.Route(false, (1, true), 1));
        Assert.Equal(FreezeEffectRule.UnfreezeRoute.Stale, FreezeEffectRule.Route(false, (2, false), 1));
        Assert.Equal(FreezeEffectRule.UnfreezeRoute.Lost, FreezeEffectRule.Route(false, null, 1));
    }

    // ---- 2. a deferred removal replays only onto the same entity ----

    [Fact]
    public void freeze_combat_a_uuid_reused_by_a_different_entity_is_not_removed_at_replay()
    {
        var d = Armed();
        foreach (var u in new long[] { 1, 2, 3 }) Assert.Equal(DeferredRemovals.Decision.Defer, d.Decide(Dead(u), Args(u)));
        var now = new Dictionary<long, nint> { [1] = Id(1), [2] = (nint)0xBEEF /* uuid 2 reused */ };   // 3: gone
        var removed = new List<long>();
        Assert.Equal(1, d.Replay(u => now.TryGetValue(u, out var p) ? p : 0, (u, _) => removed.Add(u)));
        Assert.Equal(new long[] { 1 }, removed);
        Assert.Equal(2, d.Stale);
        Assert.Equal(1, d.Replayed);

        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(4) with { Identity = 0 }, Args(4)));   // unprovable: never deferred
        Assert.Equal(0, d.Count);

        Assert.Equal(DeferredRemovals.Decision.Defer, d.Decide(Dead(5), Args(5)));
        Assert.Equal(DeferredRemovals.Decision.Defer, d.Decide(Dead(5) with { Identity = 0x5555 }, Args(5)));   // died again as a new entity
        removed.Clear();
        Assert.Equal(1, d.Replay(u => u == 5 ? 0x5555 : 0, (u, _) => removed.Add(u)));   // re-keyed to the entity it serves now
        Assert.Equal(new long[] { 5 }, removed);

        Assert.Equal(DeferredRemovals.Decision.Defer, d.Decide(Dead(6), Args(6)));
        Assert.Equal(0, d.Replay(_ => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException("unprovable")));
    }

    // ---- 9. off the main thread every removal passes ----

    [Fact]
    public void freeze_combat_removal_off_the_main_thread_passes_through()
    {
        var d = Armed();
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(1) with { OffMainThread = true }, Args(1)));
        Assert.Equal(0, d.Count);
        Assert.Equal(DeferredRemovals.Decision.Defer, d.Decide(Dead(2), Args(2)));
        Assert.Equal(DeferredRemovals.Decision.Run, d.Decide(Dead(2) with { Immediate = true, OffMainThread = true }, Args(2)));
        Assert.True(d.IsQueued(2));   // the off-thread call touched nothing
    }

    // ---- 4. the flush runs first and can never skip the teardown ----

    [Fact]
    public void freeze_combat_unfreeze_flushes_deferred_removals_before_the_teardown_and_a_throwing_flush_never_skips_it()
    {
        var steps = new Recorder();
        Assert.Null(FreezeTeardown.RunAfterFlush(() => steps.Calls.Add("Flush"), () => steps.Calls.Add("DisarmRemovals"), steps));
        Assert.Equal(new[] { "Flush", "DisarmRemovals", "DisarmGate", "StopHold", "RestoreDrawnSpeeds", "RestoreFactors",
            "RestoreEcsLayers", "UnfreezeEffects", "ClearLedger" }, steps.Calls);

        var again = new Recorder();
        var ex = FreezeTeardown.RunAfterFlush(() => throw new InvalidOperationException("flush"), () => again.Calls.Add("DisarmRemovals"), again);
        Assert.Equal("flush", Assert.IsType<InvalidOperationException>(ex).Message);
        Assert.Equal(new[] { "DisarmRemovals", "DisarmGate", "StopHold", "RestoreDrawnSpeeds", "RestoreFactors",
            "RestoreEcsLayers", "UnfreezeEffects", "ClearLedger" }, again.Calls);
    }

    // ---- 7. a leaving entity's ECS layers are written back before it is untracked ----

    [Fact]
    public void freeze_combat_leaving_entity_gets_its_ecs_layers_back_before_untracking()
    {
        var g = ArmedEcs();
        g.Track(Uid, Monster);
        var s = 1.2f;
        Assert.True(g.TrySubstitute(Uid, 1, ref s, 1f, Main));
        var writes = new List<(uint, int, float)>();
        Assert.Equal(2, g.Release(Monster, (uid, uuid) => uid == Uid && uuid == Monster ? 0.9f : null, (uid, w) => writes.Add((uid, w.Layer, w.Speed))));
        Assert.Equal(new[] { (Uid, -1, 0.9f), (Uid, 1, 1.2f) }, writes);
        Assert.False(g.Tracks(Uid));

        g.Track(Uid2, Other);
        writes.Clear();
        Assert.Equal(0, g.Release(Other, (_, _) => null, (uid, w) => writes.Add((uid, w.Layer, w.Speed))));   // model gone / recycled
        Assert.Empty(writes);
        Assert.False(g.Tracks(Uid2));

        Assert.Equal(-1, g.Release(Monster, (_, _) => throw new InvalidOperationException("untracked: never probed"), (_, _) => { }));
        g.Track(Uid, Monster);
        Assert.Throws<InvalidOperationException>(() => g.Release(Monster, (_, _) => throw new InvalidOperationException(), (_, _) => { }));
        Assert.False(g.Tracks(Uid));   // forgotten even when the probe throws
    }

    // ---- 8. each ECS writer binds only to its exact layer type ----

    [Fact]
    public void freeze_combat_ecs_patch_binds_each_writer_only_to_its_exact_layer_type()
    {
        Assert.True(EcsSpeedPatch.FitsTarget("SetAnimatorLayerData", Fake(nameof(Fakes.SetAnimatorLayerData), typeof(int))));
        Assert.False(EcsSpeedPatch.FitsTarget("SetAnimatorLayerData", Fake(nameof(Fakes.SetAnimatorLayerData), typeof(ushort))));
        Assert.True(EcsSpeedPatch.FitsTarget("PlayClip", Fake(nameof(Fakes.PlayClip), typeof(ushort))));
        Assert.False(EcsSpeedPatch.FitsTarget("PlayClip", Fake(nameof(Fakes.PlayClip), typeof(int))));
        Assert.True(EcsSpeedPatch.FitsTarget("PlayState", Fake(nameof(Fakes.PlayState), typeof(ushort))));
        Assert.False(EcsSpeedPatch.FitsTarget("PlayState", Fake(nameof(Fakes.PlayState), typeof(int))));
        Assert.True(EcsSpeedPatch.FitsTarget("PlayDynamicState", Fake(nameof(Fakes.PlayDynamicState), typeof(ushort))));
        Assert.False(EcsSpeedPatch.FitsTarget("PlayDynamicState", Fake(nameof(Fakes.PlayDynamicState), typeof(int))));
        Assert.False(EcsSpeedPatch.FitsTarget("PlayState", Fake(nameof(Fakes.PlayClip), typeof(ushort))));   // another method's name
    }

    // ---- 3. the ECS prefix call counters are diagnostics only ----

    [Fact]
    public void freeze_combat_ecs_call_counters_are_inert_with_diagnostics_off()
    {
        var g = ArmedEcs();
        foreach (EcsSpeedGate.Writer w in Enum.GetValues(typeof(EcsSpeedGate.Writer))) g.CountCall(w);
        Assert.Equal("LD:0,PS:0,PC:0,PD:0", g.CallCountsText());
    }

    // ---- helpers ----

    private static MethodInfo Fake(string name, Type layer) =>
        typeof(Fakes).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Single(m => m.Name == name && m.GetParameters()[1].ParameterType == layer);

    // The release_3.7 interop shapes, exactly (FitsTarget matches the FULL signature since the patch-safety review
    // 2026-10-02 — the fixture is the real shape: InteropShapeStandIns), each also with the wrong layer type.
    private static class Fakes
    {
        internal static void SetAnimatorLayerData(uint uid, int layer, float speed, float weight) { }
        internal static void SetAnimatorLayerData(uint uid, ushort layer, float speed, float weight) { }
        internal static uint PlayClip(uint uid, ushort layer, ECSModel.ExternalBlobPtr<ECSModel.AnimationClipBlob> clip, float fade, float normalizedTime, float speed, float weight, int mask, float end) => 0;
        internal static uint PlayClip(uint uid, int layer, ECSModel.ExternalBlobPtr<ECSModel.AnimationClipBlob> clip, float fade, float normalizedTime, float speed, float weight, int mask, float end) => 0;
        internal static uint PlayState(uint uid, ushort layer, uint hash, ref Unity.Mathematics.float2 range, float normalizedTime, float fade, float speed, float weight, int mask, float end) => 0;
        internal static uint PlayState(uint uid, int layer, uint hash, ref Unity.Mathematics.float2 range, float normalizedTime, float fade, float speed, float weight, int mask, float end) => 0;
        internal static uint PlayDynamicState(uint uid, ushort layer, ECSModel.ExternalBlobPtr<ECSModel.StateBlob> state, Unity.Mathematics.float2 range, float normalizedTime, float fade, float speed, float weight, float end) => 0;
        internal static uint PlayDynamicState(uint uid, int layer, ECSModel.ExternalBlobPtr<ECSModel.StateBlob> state, Unity.Mathematics.float2 range, float normalizedTime, float fade, float speed, float weight, float end) => 0;
    }

    private static EcsSpeedGate ArmedEcs()
    {
        var g = new EcsSpeedGate();
        g.ObserveMainThread(Main);
        g.Arm();
        return g;
    }

    private static DeferredRemovals Armed()
    {
        var d = new DeferredRemovals();
        d.Arm();
        return d;
    }

    private static nint Id(long uuid) => FreezeCombatFixTests.IdentityOf(uuid);

    private static DeferredRemovals.Call Dead(long uuid) =>
        new(uuid, DeferredRemovals.DeadType, Immediate: false, Kind: FreezeKinds.Monster, Excluded: false, Identity: Id(uuid), OffMainThread: false);

    private static object?[] Args(long uuid) => new object?[] { uuid, DeferredRemovals.DeadType, false };

    private sealed class Recorder : IFreezeTeardownSteps
    {
        public readonly List<string> Calls = new();

        public void DisarmGate() => Calls.Add(nameof(DisarmGate));
        public void StopHold() => Calls.Add(nameof(StopHold));
        public void RestoreDrawnSpeeds() => Calls.Add(nameof(RestoreDrawnSpeeds));
        public void RestoreFactors() => Calls.Add(nameof(RestoreFactors));
        public void RestoreEcsLayers() => Calls.Add(nameof(RestoreEcsLayers));
        public void UnfreezeEffects() => Calls.Add(nameof(UnfreezeEffects));
        public void ClearLedger() => Calls.Add(nameof(ClearLedger));
    }
}
