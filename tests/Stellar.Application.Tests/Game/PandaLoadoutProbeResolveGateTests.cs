using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED — the coalescing gate on the per-class gear/module resolve.
///
/// <para><b>Origin: owner report 2026-09-05</b> (the same 3,261 ms frametime spike pinned by
/// <see cref="PandaLoadoutProbeSwitchGateTests"/>). The resolve is a WHOLE-item-container walk (a uuid
/// index over every item in every package, then a gear read per slot per plan). It is event-driven — never
/// polled — but a loadout switch is a full server re-equip that emits a BURST of <c>CharSerialize</c>
/// deltas, and every one of them raises <c>SelfGearChanged</c> → <c>OnGearChanged</c> →
/// <c>_resolvePending</c>. Ungated, the walk therefore ran at the full drain rate (~30 Hz) for the length
/// of the burst; with three same-loadout switches overlapping their bursts, seconds of frame time. The
/// <c>SyncProjectList</c> refresh beside it has had a 20-tick cooldown for exactly this reason
/// (<see cref="PandaLoadoutProbeRefreshGateTests"/>); this is its twin.</para>
///
/// <para>Load-bearing invariant, identical to the refresh gate's: <b>DEFER, never DROP.</b> A resolve that
/// is due inside the cooldown yields <c>Wait</c> while the caller leaves <c>_resolvePending</c> ARMED, so
/// the burst collapses into one walk per window and the FINAL state always resolves. Nothing a container
/// merge changed may be lost — that would re-open the 2026-08-23 "same-count Replace froze the served
/// gear" class of defect.</para>
/// </summary>
public sealed class PandaLoadoutProbeResolveGateTests
{
    [Fact]
    public void An_armed_resolve_runs_when_nothing_gates_it()
    {
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Resolve,
            PandaLoadoutProbe.DecideResolve(resolvePending: true, resolverAttached: true, cooldownActive: false, hasInputs: true));
    }

    [Fact]
    public void A_burst_of_deltas_inside_the_cooldown_waits_2026_09_05_same_loadout_freeze()
    {
        // Every delta of the re-equip burst re-arms _resolvePending; only one window's worth may walk.
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(resolvePending: true, resolverAttached: true, cooldownActive: true, hasInputs: true));
    }

    [Fact]
    public void The_still_armed_resolve_runs_the_moment_the_cooldown_clears()
    {
        // DEFER, never DROP: the same armed state, re-evaluated once the window elapses, resolves — so the
        // burst's FINAL container state is always what gets served.
        var cooling = true;
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(resolvePending: true, resolverAttached: true, cooldownActive: cooling, hasInputs: true));

        cooling = false;
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Resolve,
            PandaLoadoutProbe.DecideResolve(resolvePending: true, resolverAttached: true, cooldownActive: cooling, hasInputs: true));
    }

    [Fact]
    public void Nothing_pending_waits()
    {
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(resolvePending: false, resolverAttached: true, cooldownActive: false, hasInputs: true));
    }

    [Fact]
    public void No_resolver_attached_waits()
    {
        // Host wires the resolver after the probe is built; until then there is nothing to run.
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(resolvePending: true, resolverAttached: false, cooldownActive: false, hasInputs: true));
    }

    [Fact]
    public void Nothing_saved_and_nothing_equipped_waits()
    {
        // No parsed plans and no live equipped set — the flag stays armed for the sync that brings data.
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(resolvePending: true, resolverAttached: true, cooldownActive: false, hasInputs: false));
    }
}
