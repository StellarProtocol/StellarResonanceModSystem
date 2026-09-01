using System;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED — the gates on the on-demand <c>SyncProjectList</c> / <c>RefreshChunk</c> refresh.
///
/// <para><b>Combat gate</b> (Discord issue 2026-08-25, framework-only, owner-validated in an open-world
/// boss fight): the role-plan RPC is combat-gated server-side (ErrStateIllegal 3202); the game's own
/// weapon-VM wrapper toasts "Cannot perform this action during combat". In combat the server drips
/// CharSerialize deltas ~every 5 s, each re-arming <c>_refreshPending</c>, so the rejected RPC — and its
/// toast — recurred on that cadence with zero plugins loaded.</para>
///
/// <para><b>DS-write gate</b> (owner 2026-09-01): a plugin-driven Deep-Slumber apply emits reset + one
/// activate per anchor + sockets — dozens of ops paced one-per-frame — and the server pushes a
/// CharSerialize per op, each re-arming <c>_refreshPending</c>. Without a gate the full-container
/// <c>RefreshChunk</c> walk re-fires every cooldown (~0.66 s) through the whole 1-2 s apply → 3-5 frame
/// hitches. <see cref="PandaLoadoutProbe.DecideRefresh"/> defers (like combat) while a write is in flight,
/// collapsing the burst into ONE refresh after the apply settles.</para>
///
/// <para>Load-bearing invariants (must never regress):</para>
/// <list type="number">
///   <item><b>DEFER, never DROP.</b> While in combat OR while a DS write is in flight, a due refresh
///   yields <c>DeferForCombat</c>/<c>DeferForDsWrite</c> — the caller keeps <c>_refreshPending</c> /
///   <c>!_refreshedOnce</c> armed, so exactly one refresh fires the moment the gate clears.</item>
///   <item><b>Fire once the gate clears.</b> The same armed state, re-evaluated with both gates false,
///   yields <c>Fire</c>.</item>
///   <item><b>No delegate read unless due.</b> The <c>inCombat</c>/<c>dsWriteInFlight</c> delegates are
///   consulted ONLY when a refresh is otherwise due — never on a cooldown tick, never when nothing is
///   pending — so the per-drain-tick path performs no read in the common case.</item>
///   <item><b>DS-write is checked before combat.</b> A DS apply runs out of combat, so the write gate
///   short-circuits the (Lua-backed) combat read while an apply is in flight.</item>
/// </list>
/// </summary>
public sealed class PandaLoadoutProbeRefreshGateTests
{
    // A bool probe that records whether it was consulted, so the "no read unless due" invariant is testable.
    private sealed class CountingProbe
    {
        private readonly bool _value;
        public int Calls { get; private set; }
        public CountingProbe(bool value) => _value = value;
        public bool Read() { Calls++; return _value; }
        public Func<bool> Fn => Read;
    }

    private static readonly Func<bool> NoWrite = () => false;

    [Fact]
    public void First_refresh_out_of_combat_fires()
    {
        var probe = new CountingProbe(value: false);
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Fire,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: false, refreshPending: false, cooldownActive: false, probe.Fn, NoWrite));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void First_refresh_in_combat_defers_and_stays_armed()
    {
        var probe = new CountingProbe(value: true);
        // refreshedOnce stays false in the caller on a defer — so the FIRST refresh is still owed.
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.DeferForCombat,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: false, refreshPending: false, cooldownActive: false, probe.Fn, NoWrite));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void Pending_refresh_in_combat_defers()
    {
        var probe = new CountingProbe(value: true);
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.DeferForCombat,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, probe.Fn, NoWrite));
    }

    [Fact]
    public void Pending_refresh_out_of_combat_fires_when_combat_ends()
    {
        var probe = new CountingProbe(value: false);
        // Same armed state (refreshedOnce + pending) as the deferred case above, now out of combat.
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Fire,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, probe.Fn, NoWrite));
    }

    [Fact]
    public void Cooldown_active_waits_without_reading_combat_or_write_state()
    {
        var combat = new CountingProbe(value: true);
        var write = new CountingProbe(value: true);
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Wait,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: true, combat.Fn, write.Fn));
        Assert.Equal(0, combat.Calls);   // no Lua combat read while cooling down
        Assert.Equal(0, write.Calls);    // no write read while cooling down
    }

    [Fact]
    public void Nothing_pending_waits_without_reading_combat_or_write_state()
    {
        var combat = new CountingProbe(value: true);
        var write = new CountingProbe(value: true);
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Wait,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: false, cooldownActive: false, combat.Fn, write.Fn));
        Assert.Equal(0, combat.Calls);
        Assert.Equal(0, write.Calls);
    }

    [Fact]
    public void Defer_then_end_of_combat_transitions_from_Defer_to_Fire()
    {
        // Simulates the whole run: a due refresh, combat true→false. Deferred while true, fires once false.
        var inCombat = true;
        Func<bool> probe = () => inCombat;

        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.DeferForCombat,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, probe, NoWrite));

        inCombat = false;   // combat ends
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Fire,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, probe, NoWrite));
    }

    // ── DS-write gate (owner 2026-09-01) ──────────────────────────────────────────────────────────

    [Fact]
    public void DsWrite_in_flight_defers_and_does_not_read_combat()
    {
        // A due refresh while a DS apply is in flight → DeferForDsWrite. The combat delegate must NOT be
        // consulted (DS writes run out of combat; the write gate short-circuits the Lua combat read).
        var combat = new CountingProbe(value: true);
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.DeferForDsWrite,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, combat.Fn, () => true));
        Assert.Equal(0, combat.Calls);
    }

    [Fact]
    public void DsWrite_in_flight_defers_even_on_the_first_refresh()
    {
        // Deferring keeps !_refreshedOnce armed, so the owed first refresh still fires once the apply ends.
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.DeferForDsWrite,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: false, refreshPending: false, cooldownActive: false, NoWrite, () => true));
    }

    [Fact]
    public void DsWrite_clears_then_fires()
    {
        // The apply completes: write no longer in flight, out of combat → the single settling refresh fires.
        var inFlight = true;
        Func<bool> write = () => inFlight;

        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.DeferForDsWrite,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, NoWrite, write));

        inFlight = false;   // apply settled
        Assert.Equal(PandaLoadoutProbe.RefreshOutcome.Fire,
            PandaLoadoutProbe.DecideRefresh(refreshedOnce: true, refreshPending: true, cooldownActive: false, NoWrite, write));
    }
}
