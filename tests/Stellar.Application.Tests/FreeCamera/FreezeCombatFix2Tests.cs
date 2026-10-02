using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Review findings on the combat-freeze fix (03bcdc5, report .superpowers/sdd/posing/combat-freeze-fix2-report.md), each
// pinned through its pure rule:
//  2. a deferred removal replayed by uuid alone could remove a DIFFERENT entity the uuid was reused for: the replay now
//     requires GetEntity(uuid) to serve the same native pointer kept at defer time;
//  4. a throwing flush could skip the teardown (stuck-frozen scene): FreezeTeardown.RunAfterFlush — re-pinned 2026-10-02
//     (late) on the time-pause teardown (hold release, clock resume, ledger clear): a throwing flush never skips the resume;
//  9. the removal gate passes through off the main thread.
// (1. Init-frozen effects, 7. the leaving entity's ECS layers, 8. the ECS writer binding and 3. the ECS call counters were
// removed 2026-10-02 late with the effect freeze and the ECS layer gate: the freeze is a global time pause now.) Do not weaken.
public sealed class FreezeCombatFix2Tests
{

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
        Assert.Equal(new[] { "Flush", "DisarmRemovals", "ReleaseAnim", "StopHold", "ResumeClock", "ClearLedger" }, steps.Calls);

        var again = new Recorder();
        var ex = FreezeTeardown.RunAfterFlush(() => throw new InvalidOperationException("flush"), () => again.Calls.Add("DisarmRemovals"), again);
        Assert.Equal("flush", Assert.IsType<InvalidOperationException>(ex).Message);
        Assert.Equal(new[] { "DisarmRemovals", "ReleaseAnim", "StopHold", "ResumeClock", "ClearLedger" }, again.Calls);
    }

    // ---- helpers ----

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

        public void ReleaseAnim() => Calls.Add(nameof(ReleaseAnim));
        public void StopHold() => Calls.Add(nameof(StopHold));
        public void ResumeClock() => Calls.Add(nameof(ResumeClock));
        public void ClearLedger() => Calls.Add(nameof(ClearLedger));
    }
}
