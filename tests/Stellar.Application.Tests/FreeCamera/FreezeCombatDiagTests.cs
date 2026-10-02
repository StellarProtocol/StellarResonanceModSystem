using System;
using System.Linq;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Owner reports 2026-10-02 (MAIN, framework 8aff550): in combat, frozen monsters keep animating, walk / slide, their skill
// effects keep playing, and killed ones vanish while frozen. A test-client fight was unreachable three times, so the
// owner's next fight is the only sample: a DIAGNOSTICS-ONLY evidence capture (FreezeDiagClock / FreezeDiagCounters /
// FreezeDiagVerdict + the counted set_Speed decision). Pinned: with diagnostics off the capture is inert (nothing
// scheduled, logged or counted); with it on the log volume stays bounded (2 Hz, 60 s, ≤ 20 lines/s, ≤ 80 event lines);
// the counted gate decision never drifts from the plain one. Do not weaken.
public sealed class FreezeCombatDiagTests
{
    private const int Main = 1;
    private const long Monster = 7, Self = 42;
    private static readonly IntPtr Comp = new(0x1000), SelfComp = new(0x2000), Stranger = new(0x3000);

    [Fact]
    public void freeze_combat_diag_inert_when_diagnostics_off()
    {
        var clock = new FreezeDiagClock(enabled: false);
        clock.Start(nowMs: 0);
        Assert.False(clock.Active);
        Assert.False(clock.WantsFrames(0));
        Assert.False(clock.Due(0));
        Assert.False(clock.TakeLine(0));
        Assert.False(clock.TakeEvent(0));
        Assert.Equal(0, clock.Samples);

        var c = new FreezeDiagCounters(enabled: false);
        Assert.False(c.Watch(Monster));
        c.Map(Comp, Monster);
        Assert.False(c.TryOwner(Comp, out _));
        c.Hit(Monster, DiagSlot.CtlSpeedUp);
        c.HitPtr(Comp, DiagSlot.GateSub);
        c.OffThread();
        Assert.Equal(0, c.Global(DiagSlot.CtlSpeedUp));
        Assert.Equal(0, c.Global(DiagSlot.GateSub));
        Assert.Equal(0, c.OffThreadHits);
        Assert.Equal(0, c.Watched);
        Assert.Equal(0, c.Pointers);
    }

    [Fact]
    public void freeze_combat_diag_samples_at_2hz_and_stops_after_60s()
    {
        var clock = new FreezeDiagClock(enabled: true);
        clock.Start(nowMs: 1000);
        Assert.True(clock.Due(1000));                     // the first sample is due at once
        Assert.False(clock.Due(1499));
        Assert.True(clock.Due(1500));
        Assert.Equal(2, clock.Samples);
        var due = 0;
        for (var t = 1500L; t <= 1000 + FreezeDiagClock.LifetimeMs + 5000; t += 50) if (clock.Due(t)) due++;
        Assert.InRange(due, 115, 120);                    // ~2 Hz over the remaining ~59.5 s, then nothing
        Assert.True(clock.Expired(1000 + FreezeDiagClock.LifetimeMs + 1));
        Assert.False(clock.WantsFrames(1000 + FreezeDiagClock.LifetimeMs + 1));
        Assert.False(clock.Due(1000 + FreezeDiagClock.LifetimeMs + 10_000));
    }

    [Fact]
    public void freeze_combat_diag_line_budget_is_20_per_second_and_events_cap_at_80()
    {
        var clock = new FreezeDiagClock(enabled: true);
        clock.Start(0);
        Assert.Equal(FreezeDiagClock.LinesPerSecond, Enumerable.Range(0, 50).Count(_ => clock.TakeLine(100)));
        Assert.Equal(30, clock.Dropped);
        Assert.True(clock.TakeLine(1100));                // a new one-second window
        Assert.Equal(FreezeDiagClock.EventLines, Enumerable.Range(0, 200).Count(_ => clock.TakeEvent(10)));
        Assert.False(clock.TakeEvent(FreezeDiagClock.LifetimeMs + 1));
        // The sampler's own plan fits the budget: two samples a second, a summary each, one effects line a second.
        Assert.True(2 * (1 + FreezeDiagClock.EntityLinesPerSample) + 1 <= FreezeDiagClock.LinesPerSecond);
        clock.Start(5000);                                // a new freeze resets the budget
        Assert.Equal(0, clock.Dropped);
        Assert.True(clock.TakeEvent(5000));
    }

    [Fact]
    public void freeze_combat_diag_counters_attribute_by_pointer_and_window_per_sample()
    {
        var c = new FreezeDiagCounters(enabled: true);
        Assert.True(c.Watch(Monster));
        c.Map(Comp, Monster);
        c.HitPtr(Comp, DiagSlot.CtlSpeedUp);
        c.HitPtr(Comp, DiagSlot.CtlSpeedUp);
        c.HitPtr(Stranger, DiagSlot.CtlSpeedUp);          // unmapped: global only
        c.Hit(99, DiagSlot.SkillStage);                   // not watched: global only
        Assert.Equal(3, c.Global(DiagSlot.CtlSpeedUp));
        Assert.Equal(2, c.GlobalWatched(DiagSlot.CtlSpeedUp));
        Assert.Equal(1, c.Global(DiagSlot.SkillStage));
        Assert.Equal(0, c.GlobalWatched(DiagSlot.SkillStage));

        var window = new int[FreezeDiagCounters.SlotCount];
        Assert.True(c.Take(Monster, window));
        Assert.Equal(2, window[(int)DiagSlot.CtlSpeedUp]);
        Assert.True(c.Take(Monster, window));             // the window restarted
        Assert.Equal(0, window[(int)DiagSlot.CtlSpeedUp]);
        Assert.Equal(2, c.Total(Monster, DiagSlot.CtlSpeedUp));   // the freeze total stays
        Assert.False(c.Take(99, window));

        c.Reset();
        Assert.Equal(0, c.Watched);
        Assert.False(c.TryOwner(Comp, out _));
        Assert.Equal(0, c.Global(DiagSlot.CtlSpeedUp));
    }

    [Fact]
    public void freeze_combat_diag_counters_are_bounded()
    {
        var c = new FreezeDiagCounters(enabled: true);
        for (var u = 1L; u <= FreezeDiagCounters.MaxWatched + 10; u++) c.Watch(u);
        Assert.Equal(FreezeDiagCounters.MaxWatched, c.Watched);
        for (var p = 1; p <= FreezeDiagCounters.MaxPointers + 10; p++) c.Map(new IntPtr(p), 1);
        Assert.Equal(FreezeDiagCounters.MaxPointers, c.Pointers);
        c.Map(new IntPtr(1), 2);                           // an existing pointer can still be re-mapped at the cap
        Assert.True(c.TryOwner(new IntPtr(1), out var owner) && owner == 2);
    }

    [Fact]
    public void freeze_combat_diag_counted_gate_decides_exactly_like_the_plain_gate()
    {
        foreach (var (thread, own, comp, value) in new[]
                 { (Main, false, Comp, 1.2f), (Main, true, Comp, 1.2f), (2, false, Comp, 1.2f), (0, false, Comp, 1.2f),
                   (Main, false, Stranger, 1.2f), (Main, false, SelfComp, 1.2f), (Main, false, Comp, 0f) })
        {
            var (plainGate, plainLedger) = Armed();
            var (countedGate, countedLedger) = Armed();
            plainGate.OwnWrite = countedGate.OwnWrite = own;
            var a = value;
            var b = value;
            var plain = !own && plainGate.TrySubstitute(comp, ref a, thread);
            var counted = countedGate.TrySubstituteCounted(comp, ref b, thread, new FreezeDiagCounters(enabled: true));
            Assert.Equal(plain, counted);
            Assert.Equal(a, b);
            Assert.Equal(plainGate.Seen, countedGate.Seen);
            Assert.Equal(plainLedger.Speeds.ContainsKey(Monster), countedLedger.Speeds.ContainsKey(Monster));
        }
    }

    [Fact]
    public void freeze_combat_diag_counted_gate_names_every_passthrough_reason()
    {
        var (g, _) = Armed();
        var c = new FreezeDiagCounters(enabled: true);
        c.Watch(Monster);
        c.Watch(Self);
        c.Map(Comp, Monster);
        c.Map(SelfComp, Self);
        var v = 1f;
        Assert.True(g.TrySubstituteCounted(Comp, ref v, Main, c));
        v = 1f; g.TrySubstituteCounted(Stranger, ref v, Main, c);
        v = 1f; g.TrySubstituteCounted(SelfComp, ref v, Main, c);   // tracked before the late exclusion → excluded
        v = 1f; g.TrySubstituteCounted(Comp, ref v, 2, c);
        g.OwnWrite = true;
        v = 1f; g.TrySubstituteCounted(Comp, ref v, Main, c);
        Assert.Equal(1, c.Total(Monster, DiagSlot.GateSub));
        Assert.Equal(1, c.Global(DiagSlot.GatePassUntracked));
        Assert.Equal(1, c.Total(Self, DiagSlot.GatePassExcluded));
        Assert.Equal(1, c.OffThreadHits);
        Assert.Equal(1, c.Total(Monster, DiagSlot.GatePassOwn));
        Assert.True(g.TracksFor(Comp, Monster));
        Assert.False(g.TracksFor(Comp, Self));
        Assert.True(g.TracksUuid(Monster));
    }

    [Fact]
    public void freeze_combat_diag_verdict_names_each_supported_hypothesis()
    {
        Assert.Equal(new[] { "NO-MONSTERS-SAMPLED" }, FreezeDiagVerdict.Explain(new FreezeDiagTally()));
        Assert.Equal(new[] { "ALL-HELD" }, FreezeDiagVerdict.Explain(new FreezeDiagTally { Monsters = 3 }));
        var all = FreezeDiagVerdict.Explain(new FreezeDiagTally
        {
            Monsters = 3, Untargeted = 1, NeverFrozen = 1, Untracked = 1, DrawnAnimating = 1, ControllerSpeedUp = 1, NotHeld = 1,
            PositionAfterHold = 1, OffHold = 4, EffectsMissed = 2, EffectsUnfrozen = 1, Despawns = 1,
        });
        Assert.Equal(new[]
        {
            "NOT-TARGETED", "TARGETED-NOT-FROZEN", "COMP-UNTRACKED", "DRAWN-SPEED-RESUMED", "ANIM-OTHER-DRIVER",
            "HOLD-NOT-COVERING", "POSITION-WRITTEN-AFTER-HOLD", "FX-CREATED-UNHOOKED", "FX-UNFROZEN-BY-GAME", "DESPAWNED-WHILE-FROZEN",
        }, all);
        Assert.Contains("POSITION-MOVER-BETWEEN-HOLDS", FreezeDiagVerdict.Explain(new FreezeDiagTally { Monsters = 1, OffHold = 2 }));
        Assert.Contains("ANIM-OTHER-DRIVER", FreezeDiagVerdict.Explain(new FreezeDiagTally { Monsters = 1, ControllerAnimating = 1 }));
    }

    [Fact]
    public void freeze_combat_diag_row_reports_pointer_swaps_since_the_first_sample()
    {
        var r = new FreezeDiagRow(Monster, FreezeKinds.Monster);
        Assert.False(r.NotePointers(new IntPtr(1), new IntPtr(2), new IntPtr(3), new IntPtr(4)));
        r.Samples++;
        Assert.False(r.NotePointers(new IntPtr(1), new IntPtr(2), new IntPtr(3), new IntPtr(4)));
        Assert.False(r.NotePointers(new IntPtr(1), IntPtr.Zero, new IntPtr(3), new IntPtr(4)));   // unreadable ≠ swapped
        Assert.True(r.NotePointers(new IntPtr(1), new IntPtr(9), new IntPtr(8), new IntPtr(4)));
        Assert.True(r.Swapped);
        Assert.Equal("comp,ctl", r.SwapText(new IntPtr(1), new IntPtr(9), new IntPtr(8), new IntPtr(4)));
        Assert.Equal("-", r.SwapText(new IntPtr(1), new IntPtr(2), new IntPtr(3), new IntPtr(4)));
    }

    // A gate armed on a ledger where Monster is frozen and tracked, and Self is tracked but then excluded.
    private static (DrawnSpeedGate, FreezeLedger) Armed()
    {
        var l = new FreezeLedger();
        l.Begin(self: 0);
        var g = new DrawnSpeedGate();
        g.Arm(l);
        g.ObserveMainThread(Main);
        g.Track(Comp, Monster, FreezeKinds.Monster);
        g.Track(SelfComp, Self, FreezeKinds.Char);
        l.LearnSelf(Self);                                 // learned late: still mapped, now excluded
        return (g, l);
    }
}
