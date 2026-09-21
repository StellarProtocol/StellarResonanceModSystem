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
            AttrReadOutcome.Probed(11710, read: false)));

        Assert.True(result.SkippedNotReady);
        Assert.Empty(result.Latched);
        Assert.False(memo.IsUnreadable(11020));
        Assert.False(memo.IsUnreadable(11340));
        Assert.False(memo.IsUnreadable(11710));
    }

    [Fact]
    public void a_pass_with_at_least_one_hit_latches_the_misses()
    {
        var memo = new AttrReadabilityMemo();

        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false),
            AttrReadOutcome.Probed(11980, read: false)));

        Assert.False(result.SkippedNotReady);
        Assert.Equal(new[] { 11760, 11980 }, result.Latched);
        Assert.False(memo.IsUnreadable(11020));
        Assert.True(memo.IsUnreadable(11760));
        Assert.True(memo.IsUnreadable(11980));
    }

    [Fact]
    public void forget_reprobes_the_id()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)));
        Assert.True(memo.IsUnreadable(11760));

        Assert.True(memo.Forget(11760));

        Assert.False(memo.IsUnreadable(11760));
        Assert.False(memo.Forget(11760));   // already forgotten — no-op
    }

    [Fact]
    public void clear_drops_the_memo()
    {
        var memo = new AttrReadabilityMemo();
        memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false),
            AttrReadOutcome.Probed(11980, read: false)));

        memo.Clear();

        Assert.False(memo.IsUnreadable(11760));
        Assert.False(memo.IsUnreadable(11980));
    }

    [Fact]
    public void a_miss_through_a_locked_storage_memo_never_latches()
    {
        // The mounted / blackout window: an id whose storage type is already known reads
        // nothing for a few ticks. Previously it rendered 0; it must never become "unreadable".
        var memo = new AttrReadabilityMemo();

        var result = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Memoized(11340, read: false)));

        Assert.Empty(result.Latched);
        Assert.False(memo.IsUnreadable(11340));
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
            AttrReadOutcome.Probed(11760, read: false)));

        Assert.Equal(new[] { 11760 }, result.Latched);
        Assert.True(memo.IsUnreadable(11760));
    }

    [Fact]
    public void an_already_latched_id_is_not_reported_twice()
    {
        var memo = new AttrReadabilityMemo();
        var first = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)));
        Assert.Single(first.Latched);

        var second = memo.Record(Pass(
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11760, read: false)));

        Assert.Empty(second.Latched);
        Assert.True(memo.IsUnreadable(11760));
    }

    [Fact]
    public void an_empty_pass_is_inert()
    {
        var memo = new AttrReadabilityMemo();

        var result = memo.Record(Pass());

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
            AttrReadOutcome.Probed(11760, read: false)));

        Assert.Equal(3, result.Attempted);
        Assert.Equal(2, result.Hits);
    }

    private static IReadOnlyList<AttrReadOutcome> Pass(params AttrReadOutcome[] outcomes) => outcomes;
}
