using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class InstancePruneRuleTests
{
    [Fact]
    public void NotInLedger_returns_only_uids_the_ledger_no_longer_holds()
    {
        var ledger = new HashSet<long> { 1, 3 };
        var drop = InstancePruneRule.NotInLedger(new long[] { 1, 2, 3, 4 }, ledger.Contains);
        Assert.Equal(new long[] { 2, 4 }, drop);
    }

    [Fact]
    public void NotInLedger_empty_when_everything_still_held()
    {
        var ledger = new HashSet<long> { 1, 2 };
        Assert.Empty(InstancePruneRule.NotInLedger(new long[] { 1, 2 }, ledger.Contains));
    }

    [Fact]
    public void OverBudget_does_nothing_under_or_at_threshold_and_never_consults_state()
    {
        var calls = 0;
        var drop = InstancePruneRule.OverBudget(new long[] { 1, 2, 3 }, count: 3, threshold: 3,
            _ => { calls++; return EffectReleaseState.Ended; });
        Assert.Empty(drop);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void OverBudget_drops_only_ended_entries_once_over_threshold()
    {
        EffectReleaseState State(long uid) => uid % 2 == 0 ? EffectReleaseState.Ended : EffectReleaseState.Instance;
        var drop = InstancePruneRule.OverBudget(new long[] { 1, 2, 3, 4 }, count: 300, threshold: 256, State);
        Assert.Equal(new long[] { 2, 4 }, drop);
    }
}
