// tests/Stellar.Application.Tests/PlayerStats/PlayerStatsServiceTests.cs
using System.Collections.Generic;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.PlayerStats;

public sealed class PlayerStatsServiceTests
{
    [Fact]
    public void IsAvailable_FalseByDefault()
    {
        var svc = new PlayerStatsService(new AttrReadabilityMemo());
        Assert.False(svc.IsAvailable);
    }

    [Fact]
    public void TryGetAttribute_ReturnsNull_WhenUnsubscribed()
    {
        var svc = new PlayerStatsService(new AttrReadabilityMemo());
        Assert.Null(svc.TryGetAttribute(11011));
    }

    [Fact]
    public void TryGetAttribute_ReturnsValue_AfterSubscribeAndRefresh()
    {
        var (svc, probe) = NewService();
        svc.Subscribe(11011);
        probe.NextValues = new Dictionary<int, long> { [11011] = 525 };

        svc.Refresh(probe);

        Assert.Equal(525, svc.TryGetAttribute(11011));
    }

    [Fact]
    public void Subscribe_IsIdempotent()
    {
        var (svc, probe) = NewService();
        svc.Subscribe(11011);
        svc.Subscribe(11011);
        svc.Refresh(probe);

        Assert.Single(probe.SubscribedSnapshots[0]);
        Assert.Equal(11011, probe.SubscribedSnapshots[0][0]);
    }

    [Fact]
    public void Unsubscribe_ReturnsToNull()
    {
        var (svc, probe) = NewService();
        svc.Subscribe(11011);
        probe.NextValues = new Dictionary<int, long> { [11011] = 525 };
        svc.Refresh(probe);
        Assert.Equal(525, svc.TryGetAttribute(11011));

        svc.Unsubscribe(11011);
        probe.NextValues = new Dictionary<int, long>();
        svc.Refresh(probe);

        Assert.Null(svc.TryGetAttribute(11011));
    }

    [Fact]
    public void Unsubscribe_OnUnknownId_IsNoOp()
    {
        var svc = new PlayerStatsService(new AttrReadabilityMemo());
        // Must not throw.
        svc.Unsubscribe(99999);
    }

    [Fact]
    public void Refresh_FailedProbe_LeavesIsAvailableFalse()
    {
        var (svc, probe) = NewService();
        probe.TrySampleReturns = false;

        svc.Refresh(probe);

        Assert.False(svc.IsAvailable);
        Assert.Null(svc.TryGetAttribute(11011));
    }

    [Fact]
    public void Refresh_SuccessfulProbe_FlipsIsAvailableTrue()
    {
        var (svc, probe) = NewService();
        probe.TrySampleReturns = true;
        probe.NextValues = new Dictionary<int, long>();

        svc.Refresh(probe);

        Assert.True(svc.IsAvailable);
    }

    [Fact]
    public void Refresh_PassesSnapshotOfSubscriptionSet_NotLiveReference()
    {
        var (svc, probe) = NewService();
        svc.Subscribe(11011);
        svc.Subscribe(11021);

        svc.Refresh(probe);

        Assert.Equal(2, probe.SubscribedSnapshots[0].Length);
    }

    [Fact]
    public void Refresh_SwapsValuesDict_Atomically()
    {
        var (svc, probe) = NewService();
        svc.Subscribe(11011);
        probe.NextValues = new Dictionary<int, long> { [11011] = 525 };
        svc.Refresh(probe);

        probe.NextValues = new Dictionary<int, long> { [11011] = 999 };
        svc.Refresh(probe);

        Assert.Equal(999, svc.TryGetAttribute(11011));
    }

    [Fact]
    public void TryGetAttribute_ConcurrentRead_DoesNotThrow()
    {
        var (svc, probe) = NewService();
        svc.Subscribe(11011);
        probe.NextValues = new Dictionary<int, long> { [11011] = 525 };
        svc.Refresh(probe);

        for (var i = 0; i < 1000; i++)
        {
            _ = svc.TryGetAttribute(11011);
        }
    }

    [Fact]
    public void Subscribe_ForgetsAnUnreadableVerdict_SoTheIdIsReprobed()
    {
        // F2: re-ticking a stat in the picker is the in-game recovery from a memo that
        // latched during the login window — it must not need a client relaunch.
        var memo = new AttrReadabilityMemo();
        memo.Record(new[]
        {
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11710, read: false),
        }, sheetReady: false);
        Assert.True(memo.IsUnreadable(11710));

        new PlayerStatsService(memo).Subscribe(11710);

        Assert.False(memo.IsUnreadable(11710));
    }

    [Fact]
    public void ClearSession_DropsTheAttrReadabilityMemo()
    {
        // F3: a re-login inside the same process must not inherit a poisoned memo.
        var memo = new AttrReadabilityMemo();
        memo.Record(new[]
        {
            AttrReadOutcome.Probed(11020, read: true),
            AttrReadOutcome.Probed(11710, read: false),
        }, sheetReady: false);
        Assert.True(memo.IsUnreadable(11710));

        new PlayerStatsService(memo).ClearSession();

        Assert.False(memo.IsUnreadable(11710));
    }

    private static (PlayerStatsService, StubPlayerStatsProbe) NewService()
        => (new PlayerStatsService(new AttrReadabilityMemo()), new StubPlayerStatsProbe());
}
