using System;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED — the combat gate on the on-demand <c>SyncProjectList</c> refresh RPC (Discord issue
/// 2026-08-25, framework-only, owner-validated in an open-world boss fight).
///
/// <para>The role-plan RPC is combat-gated server-side (ErrStateIllegal 3202); the game's own weapon-VM
/// wrapper toasts "Cannot perform this action during combat". In combat the server drips CharSerialize
/// deltas ~every 5 s, each re-arming <c>_refreshPending</c>, so the rejected RPC — and its toast —
/// recurred on that cadence with zero plugins loaded. <see cref="PandaLoadoutProbe.DecideRefresh"/> gates
/// the RPC on combat while keeping the re-arm intact.</para>
///
/// <para>Load-bearing invariants (must never regress):</para>
/// <list type="number">
///   <item><b>DEFER, never DROP.</b> While in combat a due refresh yields <c>DeferForCombat</c> — the
///   caller keeps <c>_refreshPending</c> / <c>!_refreshedOnce</c> armed, so exactly one refresh fires the
///   moment combat ends. Deleting the re-arm (the tempting "fix") would drop real out-of-combat plan
///   changes.</item>
///   <item><b>Fire once combat ends.</b> The same armed state, re-evaluated with <c>inCombat = false</c>,
///   yields <c>Fire</c>.</item>
///   <item><b>No combat read unless due.</b> The <c>inCombat</c> delegate is consulted ONLY when a refresh
///   is otherwise due — never on a cooldown tick, never when nothing is pending — so the per-drain-tick
///   path performs no Lua read in the common case.</item>
/// </list>
/// </summary>
public sealed class PandaLoadoutProbeRefreshGateTests
{
    // A combat probe that records whether it was consulted, so the "no read unless due" invariant is testable.
    private sealed class CountingProbe
    {
        private readonly bool _inCombat;
        public int Calls { get; private set; }
        public CountingProbe(bool inCombat) => _inCombat = inCombat;
        public bool Read() { Calls++; return _inCombat; }
        public Func<bool> Fn => Read;
    }

    [Fact]
    public void First_refresh_out_of_combat_fires()
    {
        var probe = new CountingProbe(inCombat: false);
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Fire,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: false, refreshPending: false, cooldownActive: false, probe.Fn));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void First_refresh_in_combat_defers_and_stays_armed()
    {
        var probe = new CountingProbe(inCombat: true);
        // refreshedOnce stays false in the caller on a defer — so the FIRST refresh is still owed.
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.DeferForCombat,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: false, refreshPending: false, cooldownActive: false, probe.Fn));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void Pending_refresh_in_combat_defers()
    {
        var probe = new CountingProbe(inCombat: true);
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.DeferForCombat,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, probe.Fn));
    }

    [Fact]
    public void Pending_refresh_out_of_combat_fires_when_combat_ends()
    {
        var probe = new CountingProbe(inCombat: false);
        // Same armed state (refreshedOnce + pending) as the deferred case above, now out of combat.
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Fire,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, probe.Fn));
    }

    [Fact]
    public void Cooldown_active_waits_without_reading_combat_state()
    {
        var probe = new CountingProbe(inCombat: true);
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Wait,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: true, probe.Fn));
        Assert.Equal(0, probe.Calls);   // no Lua combat read while cooling down
    }

    [Fact]
    public void Nothing_pending_waits_without_reading_combat_state()
    {
        var probe = new CountingProbe(inCombat: true);
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Wait,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: false, cooldownActive: false, probe.Fn));
        Assert.Equal(0, probe.Calls);   // no Lua combat read when nothing is due
    }

    [Fact]
    public void Defer_then_end_of_combat_transitions_from_Defer_to_Fire()
    {
        // Simulates the whole run: a due refresh, combat true→false. Deferred while true, fires once false.
        var inCombat = true;
        Func<bool> probe = () => inCombat;

        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.DeferForCombat,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, probe));

        inCombat = false;   // combat ends
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Fire,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, probe));
    }
}
