using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Party;

/// <summary>
/// A2 transport (2026-07-17 sync spec): TeamMemberFastSyncData.state (field 6) — parsed since
/// day one, dropped at ApplyFastFields — now rides MemberSlot -> PartyMember.FastSyncState as a
/// RAW value. Semantics are calibrated in-game; the framework only transports.
/// </summary>
public sealed class PartyFastSyncStateTests
{
    private static PartyService NewService()
        => new PartyService(new StubCombat(), new StubClientState(), new StubLog());

    [Fact]
    public void FastSync_state_lands_on_the_member()
    {
        var svc = NewService();
        svc.EnqueueMemberFastSync(42, new PartyMemberFastSync(10, Position3D.Zero, 500, 1000, StateRaw: 3));
        PartyServiceTests.InvokeDrain(svc);
        var m = Assert.Single(svc.Members);
        Assert.Equal(3, m.FastSyncState);
        Assert.Equal(500L, m.Hp);
    }

    [Fact]
    public void State_only_change_fires_MemberUpdated()
    {
        var svc = NewService();
        svc.EnqueueMemberFastSync(42, new PartyMemberFastSync(10, Position3D.Zero, 500, 1000, StateRaw: 0));
        PartyServiceTests.InvokeDrain(svc);
        var updated = new List<PartyMember>();
        svc.MemberUpdated += m => updated.Add(m);
        // Same hp/maxHp/scene/position — ONLY state moves. Must still fire (a death/offline flip
        // with unchanged hp digits would otherwise never reach the meter).
        svc.EnqueueMemberFastSync(42, new PartyMemberFastSync(10, Position3D.Zero, 500, 1000, StateRaw: 2));
        PartyServiceTests.InvokeDrain(svc);
        var m = Assert.Single(updated);
        Assert.Equal(2, m.FastSyncState);
    }

    [Fact]
    public void Roster_fastsync_leg_carries_state()
    {
        var svc = NewService();
        var roster = new List<PartyMemberRoster>
        {
            new(CharId: 7, EnterTimeRaw: 100, OnlineStatusRaw: 1, SceneId: 10, GroupId: 0,
                FastSync: new PartyMemberFastSync(10, Position3D.Zero, 900, 1000, StateRaw: 5),
                Social: null),
        };
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 7, PartyType.Regular5, false, roster));
        PartyServiceTests.InvokeDrain(svc);
        Assert.Equal(5, Assert.Single(svc.Members).FastSyncState);
    }
}
