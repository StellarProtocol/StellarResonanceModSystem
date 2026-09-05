using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED — the debounce gate on the per-class gear/module resolve.
///
/// <para><b>Origin: owner report 2026-09-05</b> (the same 3,261 ms frametime spike pinned by
/// <see cref="PandaLoadoutProbeSwitchGateTests"/>). The resolve is a WHOLE-item-container walk (a uuid
/// index over every item in every package, then a gear read per slot per plan). It is event-driven — never
/// polled — but a loadout switch is a full server re-equip that emits a BURST of <c>CharSerialize</c>
/// deltas, and every one of them raises <c>SelfGearChanged</c> → <c>OnGearChanged</c> →
/// <c>_resolvePending</c>. Ungated, the walk therefore ran at the full drain rate (~30 Hz) for the length
/// of the burst; with three same-loadout switches overlapping their bursts, seconds of frame time.</para>
///
/// <para>The first fix was a LEADING-edge cooldown (walk now, then wait a window). It bounded the rate but
/// still walked THROUGH the burst — a ~1 s burst cost two walks, both against half-applied state. This is
/// the trailing-edge form: every new delta restarts a quiet timer and the walk runs once the burst has
/// STOPPED, so one loadout switch costs ONE walk, against the burst's final state.</para>
///
/// <para>Load-bearing invariant, unchanged from the cooldown form: <b>DEFER, never DROP.</b> An armed
/// resolve that is held back stays armed, so the FINAL state always resolves. Nothing a container merge
/// changed may be lost — that would re-open the 2026-08-23 "same-count Replace froze the served gear"
/// class of defect. And lateness is BOUNDED: the defer cap forces a walk during an endless delta stream,
/// so "late, never stale" holds with a ceiling rather than an open end.</para>
/// </summary>
public sealed class PandaLoadoutProbeResolveGateTests
{
    // Mirrors of the production constants (private consts on the probe) — kept as locals so a change to
    // either one shows up here as a failing pin rather than a silently re-tuned gate.
    private const int QuietTicks = 15;
    private const int MaxDefer   = 60;

    [Fact]
    public void An_armed_resolve_runs_once_the_burst_goes_quiet()
    {
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Resolve,
            PandaLoadoutProbe.DecideResolve(
                resolveArmed: true, resolverAttached: true, hasInputs: true,
                quietTicks: QuietTicks, deferTicks: 1));
    }

    [Fact]
    public void A_burst_of_deltas_still_arriving_waits_2026_09_05_same_loadout_freeze()
    {
        // Every delta of the re-equip burst restarts the quiet window; nothing walks while it is arriving.
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(
                resolveArmed: true, resolverAttached: true, hasInputs: true,
                quietTicks: 0, deferTicks: 1));

        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(
                resolveArmed: true, resolverAttached: true, hasInputs: true,
                quietTicks: QuietTicks - 1, deferTicks: QuietTicks - 1));
    }

    [Fact]
    public void The_still_armed_resolve_runs_the_moment_the_deltas_stop()
    {
        // DEFER, never DROP: the same armed state, re-evaluated once the stream goes quiet, resolves — so
        // the burst's FINAL container state is always what gets served.
        var quiet = 0;
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(
                resolveArmed: true, resolverAttached: true, hasInputs: true,
                quietTicks: quiet, deferTicks: 3));

        quiet = QuietTicks;
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Resolve,
            PandaLoadoutProbe.DecideResolve(
                resolveArmed: true, resolverAttached: true, hasInputs: true,
                quietTicks: quiet, deferTicks: 3 + QuietTicks));
    }

    [Fact]
    public void An_endless_delta_stream_is_forced_to_walk_at_the_defer_cap()
    {
        // Bounded lateness: a stream that never goes quiet must not postpone the walk forever, or the
        // served gear would be stale for as long as the game keeps merging.
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Resolve,
            PandaLoadoutProbe.DecideResolve(
                resolveArmed: true, resolverAttached: true, hasInputs: true,
                quietTicks: 0, deferTicks: MaxDefer));
    }

    [Fact]
    public void Nothing_armed_never_runs()
    {
        // Not even at the defer cap — the cap forces a PENDING walk, it never invents one.
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(
                resolveArmed: false, resolverAttached: true, hasInputs: true,
                quietTicks: QuietTicks, deferTicks: MaxDefer));
    }

    [Fact]
    public void No_resolver_attached_waits()
    {
        // Host wires the resolver after the probe is built; until then there is nothing to run.
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(
                resolveArmed: true, resolverAttached: false, hasInputs: true,
                quietTicks: QuietTicks, deferTicks: MaxDefer));
    }

    [Fact]
    public void Nothing_saved_and_nothing_equipped_waits()
    {
        // No parsed plans and no live equipped set — the arm stays up for the sync that brings data, and
        // not even the defer cap may spend a walk on nothing.
        Assert.Equal(PandaLoadoutProbe.ResolveOutcome.Wait,
            PandaLoadoutProbe.DecideResolve(
                resolveArmed: true, resolverAttached: true, hasInputs: false,
                quietTicks: QuietTicks, deferTicks: MaxDefer));
    }

    /// <summary>PINNED: one loadout switch = ONE walk. Replays a realistic burst — a delta on ~half the
    /// ticks for ~1 s, then silence — through the gate exactly as the drain tick does, and counts the
    /// walks. The leading-edge cooldown this replaced scored 2 on the same input (it walked immediately,
    /// then again a window later, both times mid-burst).</summary>
    [Fact]
    public void One_switch_burst_costs_one_walk_2026_09_05_hotkey_spike()
    {
        var (walks, lastWalkTick) = ReplayBurst(burstTicks: 30, everyNthTick: 2, totalTicks: 90);

        Assert.Equal(1, walks);
        // …and it lands AFTER the burst ended, not during it — the whole point of the trailing edge.
        Assert.True(lastWalkTick >= 30, $"walk landed at tick {lastWalkTick}, inside the burst");
    }

    /// <summary>PINNED: an endless stream still walks, on the cap. Same replay, but the deltas never
    /// stop — the gate must keep serving fresh data at a bounded rate instead of starving.</summary>
    [Fact]
    public void An_endless_stream_still_walks_on_the_cap()
    {
        var (walks, _) = ReplayBurst(burstTicks: 300, everyNthTick: 2, totalTicks: 300);

        Assert.True(walks >= 4, $"expected the defer cap to force walks, got {walks}");
        Assert.True(walks <= 6, $"expected the cap to bound them to ~1 per {MaxDefer} ticks, got {walks}");
    }

    /// <summary>PINNED: a delta on EVERY tick must still walk. Counting only quiet ticks toward the defer
    /// cap would starve the walk completely under a saturating stream — the served gear would freeze for
    /// as long as the game kept merging, which is the 2026-08-23 stale-mirror class of defect.</summary>
    [Fact]
    public void A_delta_on_every_tick_does_not_starve_the_walk()
    {
        var (walks, _) = ReplayBurst(burstTicks: 300, everyNthTick: 1, totalTicks: 300);

        Assert.True(walks >= 4, $"a saturating delta stream starved the walk (got {walks})");
    }

    // Drives DecideResolve with the same counter bookkeeping the drain tick performs
    // (ObservePerClassResolveArm), so these pins exercise the gate as wired, not just the predicate.
    private static (int Walks, int LastWalkTick) ReplayBurst(int burstTicks, int everyNthTick, int totalTicks)
    {
        var armed = false;
        var quiet = 0;
        var defer = 0;
        var walks = 0;
        var lastWalkTick = -1;

        for (var tick = 0; tick < totalTicks; tick++)
        {
            var delta = tick < burstTicks && tick % everyNthTick == 0;
            if (delta)
            {
                if (!armed) { armed = true; defer = 0; }
                quiet = 0;
            }
            else if (armed)
            {
                if (quiet < QuietTicks) quiet++;
            }

            if (armed && defer < MaxDefer) defer++;

            if (PandaLoadoutProbe.DecideResolve(armed, resolverAttached: true, hasInputs: true, quiet, defer)
                == PandaLoadoutProbe.ResolveOutcome.Wait) continue;

            armed = false;
            quiet = 0;
            defer = 0;
            walks++;
            lastWalkTick = tick;
        }

        return (walks, lastWalkTick);
    }
}
