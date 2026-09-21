using System;
using System.Collections.Generic;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.PlayerStats;

/// <summary>
/// Pins <see cref="AttrReadabilityMemo"/> — the decision extracted out of Infrastructure's
/// <c>PandaPlayerStatsProbe</c>. The first pin is the regression pin for the 2026-09-21
/// owner report (StatInspector showing eight "—" after a fast login): a sampling pass taken
/// before the game's attribute sheet is populated misses every id, and latching those ids
/// poisoned the probe for the whole process.
/// </summary>
public sealed class AttrReadabilityMemoTests
{
    [Fact]
    public void a_pass_in_which_every_id_missed_latches_nothing()
    {
        var memo = new AttrReadabilityMemo();

        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: false),
            AttrReadOutcome.Probed(11340, read: false),
            AttrReadOutcome.Probed(11710, read: false)), sheetReady: false, nowTicks: T0);

        Assert.True(result.SkippedNotReady);
        Assert.Empty(result.Latched);
        Assert.False(memo.IsUnreadable(11020, T0));
        Assert.False(memo.IsUnreadable(11340, T0));
        Assert.False(memo.IsUnreadable(11710, T0));
    }

    [Fact]
    public void a_pass_before_readiness_with_all_misses_latches_nothing()
    {
        var memo = new AttrReadabilityMemo();

        // The login window: the entity exists, the attribute sheet does not yet.
        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11760, read: false),
            AttrReadOutcome.Probed(11980, read: false)), sheetReady: false, nowTicks: T0);

        Assert.True(result.SkippedNotReady);
        Assert.Empty(result.Latched);
        Assert.False(memo.IsUnreadable(11760, T0));
        Assert.False(memo.IsUnreadable(11980, T0));
    }

    [Fact]
    public void a_pass_where_only_the_readiness_signal_is_true_latches_the_misses()
    {
        var memo = new AttrReadabilityMemo();

        // A CombatMeter-only client subscribes exactly 11760 + 11980, both absent from the
        // wire sheet. Every pass is all-miss forever, so readiness can never be inferred from
        // the pass — without the explicit signal these ids never latch and the probe re-probes
        // them (3 reflective Invokes each) every tick for the process lifetime.
        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11760, read: false),
            AttrReadOutcome.Probed(11980, read: false)), sheetReady: true, nowTicks: T0);

        Assert.False(result.SkippedNotReady);
        Assert.Equal(new[] { 11760, 11980 }, result.Latched);
        Assert.True(memo.IsUnreadable(11760, T0));
        Assert.True(memo.IsUnreadable(11980, T0));
    }

    [Fact]
    public void readiness_never_latches_a_miss_through_a_locked_storage_memo()
    {
        var memo = new AttrReadabilityMemo();

        // Sheet ready but this id missed through an already-locked storage type — a blackout,
        // not an absent attribute. Readiness must not turn that into a permanent verdict.
        var result = memo.Record(Pass(
            AttrReadOutcome.Memoized(11020, read: false),
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: true, nowTicks: T0);

        Assert.Equal(new[] { 11760 }, result.Latched);
        Assert.False(memo.IsUnreadable(11020, T0));
    }

    [Fact]
    public void a_pass_with_at_least_one_hit_latches_the_misses()
    {
        var memo = new AttrReadabilityMemo();

        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false),
            AttrReadOutcome.Probed(11980, read: false)), sheetReady: false, nowTicks: T0);

        Assert.False(result.SkippedNotReady);
        Assert.Equal(new[] { 11760, 11980 }, result.Latched);
        Assert.False(memo.IsUnreadable(11020, T0));
        Assert.True(memo.IsUnreadable(11760, T0));
        Assert.True(memo.IsUnreadable(11980, T0));
    }

    [Fact]
    public void forget_reprobes_the_id()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: false, nowTicks: T0);
        Assert.True(memo.IsUnreadable(11760, T0));

        Assert.True(memo.Forget(11760));

        Assert.False(memo.IsUnreadable(11760, T0));
        Assert.False(memo.Forget(11760));   // already forgotten — no-op
    }

    [Fact]
    public void clear_drops_the_memo()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false),
            AttrReadOutcome.Probed(11980, read: false)), sheetReady: false, nowTicks: T0);

        memo.Clear();

        Assert.False(memo.IsUnreadable(11760, T0));
        Assert.False(memo.IsUnreadable(11980, T0));
    }

    [Fact]
    public void a_miss_through_a_locked_storage_memo_never_latches()
    {
        // The mounted / blackout window: an id whose storage type is already known reads
        // nothing for a few ticks. Previously it rendered 0; it must never become "unreadable".
        var memo = new AttrReadabilityMemo();

        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Memoized(11340, read: false)), sheetReady: false, nowTicks: T0);

        Assert.Empty(result.Latched);
        Assert.False(memo.IsUnreadable(11340, T0));
    }

    [Fact]
    public void a_hit_through_a_locked_storage_memo_still_counts_as_sheet_ready()
    {
        // A stat subscribed at runtime (StatInspector's picker) joins a pass in which every
        // other id is already memoized. Those hits are the readiness evidence that lets the
        // newcomer's full-probe miss latch — otherwise it would re-probe (and log) forever.
        var memo = new AttrReadabilityMemo();

        var result = memo.Record(Pass(
            AttrReadOutcome.Memoized(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: false, nowTicks: T0);

        Assert.Equal(new[] { 11760 }, result.Latched);
        Assert.True(memo.IsUnreadable(11760, T0));
    }

    [Fact]
    public void an_already_latched_id_is_not_reported_twice()
    {
        var memo = new AttrReadabilityMemo();
        var first = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: false, nowTicks: T0);
        Assert.Single(first.Latched);

        var second = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: false, nowTicks: T0);

        Assert.Empty(second.Latched);
        Assert.True(memo.IsUnreadable(11760, T0));
    }

    [Fact]
    public void an_empty_pass_is_inert()
    {
        var memo = new AttrReadabilityMemo();

        var result = memo.Record(Pass(), sheetReady: false, nowTicks: T0);

        Assert.False(result.SkippedNotReady);
        Assert.Empty(result.Latched);
        Assert.Equal(0, result.Attempted);
    }

    [Fact]
    public void pass_result_reports_attempted_and_hit_counts()
    {
        var memo = new AttrReadabilityMemo();

        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Memoized(11340, read: true),
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: false, nowTicks: T0);

        Assert.Equal(3, result.Attempted);
        Assert.Equal(2, result.Hits);
    }

    [Fact]
    public void only_an_all_miss_pass_holding_a_first_read_probe_needs_the_readiness_signal()
    {
        // The probe pays for the readiness read exactly here …
        Assert.True(AttrReadabilityMemo.NeedsReadinessSignal(Pass(
            AttrReadOutcome.Probed(11760, read: false),
            AttrReadOutcome.Probed(11980, read: false))));

        // … and nowhere else: a pass that read something decides itself, a pass that can
        // latch nothing has nothing to decide, and an empty pass is inert.
        Assert.False(AttrReadabilityMemo.NeedsReadinessSignal(Pass(
            AttrReadOutcome.Memoized(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false))));
        Assert.False(AttrReadabilityMemo.NeedsReadinessSignal(Pass(
            AttrReadOutcome.Memoized(11020, read: false))));
        Assert.False(AttrReadabilityMemo.NeedsReadinessSignal(Pass()));
    }

    [Fact]
    public void a_latched_id_is_re_probed_after_the_ttl_and_not_before()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11951, read: false)), sheetReady: false, nowTicks: T0);

        // Inside the window the verdict holds — that is the anti-spam the memo exists for.
        Assert.True(memo.IsUnreadable(11951, T0));
        Assert.True(memo.IsUnreadable(11951, T0 + Seconds(59)));

        // Past it the id is probed again, so an attribute the game publishes seconds after
        // max HP / level (11951 latched on the owner's test client, 2026-09-22 run 1) shows
        // up within a minute instead of dashing until the client is relaunched.
        Assert.False(memo.IsUnreadable(11951, T0 + Seconds(61)));
    }

    [Fact]
    public void a_re_probe_that_still_misses_re_latches_and_restarts_the_window()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: false, nowTicks: T0);

        var reProbe = T0 + Seconds(61);
        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: false, nowTicks: reProbe);

        // A genuinely absent id (11760) costs one re-probe per window, for ever — never a tick loop.
        Assert.Equal(new[] { 11760 }, result.Latched);
        Assert.True(memo.IsUnreadable(11760, reProbe));
        Assert.True(memo.IsUnreadable(11760, reProbe + Seconds(59)));   // measured from the RE-probe…
        Assert.False(memo.IsUnreadable(11760, reProbe + Seconds(61)));  // …not from the first latch
    }

    [Fact]
    public void the_anti_spam_bound_holds_inside_the_window()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: false, nowTicks: T0);

        // One window at the 60 Hz framework tick = 3 600 passes. Not one of them may re-probe
        // or re-report the id: the retry costs ~3 reflective invokes per absent id per MINUTE.
        const long tickTicks = TimeSpan.TicksPerSecond / 60;
        for (var tick = 1; tick < 3600; tick++)
        {
            var now = T0 + (tick * tickTicks);
            Assert.True(memo.IsUnreadable(11760, now));
            var result = memo.Record(Pass(
                AttrReadOutcome.Probed(11020, read: true),
                AttrReadOutcome.Probed(11760, read: false)), sheetReady: false, nowTicks: now);
            Assert.Empty(result.Latched);
        }
    }

    [Fact]
    public void a_re_probe_that_hits_clears_the_id()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11951, read: false)), sheetReady: false, nowTicks: T0);
        Assert.True(memo.IsUnreadable(11951, T0));

        // The late attribute answers on the retry pass: the verdict is dropped outright, not
        // re-stamped, so the stat renders from here on and costs nothing more.
        var late = T0 + Seconds(61);
        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11951, read: true)), sheetReady: false, nowTicks: late);

        Assert.Empty(result.Latched);
        Assert.False(memo.IsUnreadable(11951, late));
        Assert.False(memo.IsUnreadable(11951, late + Seconds(3600)));
    }

    [Fact]
    public void a_not_ready_pass_refreshes_an_existing_verdict_but_never_creates_one()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: true, nowTicks: T0);
        Assert.True(memo.IsUnreadable(11760, T0));

        // The window lapses, so the probe re-probes 11760 — and this pass finds the sheet DARK
        // (all-miss, readiness false). Discarding it untouched would leave the expired stamp in
        // place and re-probe the id EVERY TICK until the sheet answers (PR #88 review, IMP-1).
        var dark = T0 + Seconds(61);
        Assert.False(memo.IsUnreadable(11760, dark));

        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11760, read: false),
            AttrReadOutcome.Probed(11980, read: false)), sheetReady: false, nowTicks: dark);

        Assert.True(result.SkippedNotReady);
        Assert.Empty(result.Latched);                                  // no NEW verdict is reported
        Assert.True(memo.IsUnreadable(11760, dark + Seconds(1)));      // …the existing one restarts
        Assert.True(memo.IsUnreadable(11760, dark + Seconds(59)));
        Assert.False(memo.IsUnreadable(11760, dark + Seconds(60)));    // bounded, as always

        // F1 is untouched: an id with no entry is not latched by a pass over a dark sheet.
        Assert.False(memo.IsUnreadable(11980, dark));
        Assert.False(memo.IsUnreadable(11980, dark + Seconds(1)));
    }

    [Fact]
    public void the_retry_window_ends_at_exactly_sixty_seconds()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: true, nowTicks: T0);

        // `age < RetryAfterTicks`: the last in-window instant is 60 s minus one tick.
        Assert.True(memo.IsUnreadable(11760, T0 + Millis(59_999)));
        Assert.True(memo.IsUnreadable(11760, T0 + Seconds(60) - 1));
        Assert.False(memo.IsUnreadable(11760, T0 + Seconds(60)));
    }

    [Fact]
    public void a_clock_reading_behind_the_stamp_reads_as_expired()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: true, nowTicks: T0);
        Assert.True(memo.IsUnreadable(11760, T0));

        // A negative age fails OPEN (re-probe), never into a frozen window.
        Assert.False(memo.IsUnreadable(11760, T0 - Millis(1)));
        Assert.False(memo.IsUnreadable(11760, T0 - Seconds(3600)));

        // …and the next miss re-stamps normally from the instant it was taken.
        var later = T0 + Seconds(61);
        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)), sheetReady: false, nowTicks: later);

        Assert.Equal(new[] { 11760 }, result.Latched);
        Assert.True(memo.IsUnreadable(11760, later + Seconds(59)));
        Assert.False(memo.IsUnreadable(11760, later + Seconds(60)));
    }

    private static IReadOnlyList<AttrReadOutcome> Pass(params AttrReadOutcome[] outcomes) => outcomes;

    /// <summary>A fixed clock origin — every pin states its own instants relative to it.</summary>
    private const long T0 = 638_000_000_000_000_000L;

    private static long Seconds(long s) => s * TimeSpan.TicksPerSecond;

    private static long Millis(long ms) => ms * TimeSpan.TicksPerMillisecond;
}
