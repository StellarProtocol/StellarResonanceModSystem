using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Party;

public sealed class PartyServiceTests
{
    private static PartyService NewService(out StubCombat combat, out StubClientState clientState)
    {
        combat      = new StubCombat();
        clientState = new StubClientState();
        var log     = new StubLog();
        return new PartyService(combat, clientState, log);
    }

    [Fact]
    public void ApplyFullSnapshot_FirstDelivery_FiresMemberJoinedForEach()
    {
        var svc = NewService(out _, out _);
        var joined = new List<PartyMember>();
        svc.MemberJoined += m => joined.Add(m);

        var roster = new List<PartyMemberRoster>
        {
            new(CharId: 1, EnterTimeRaw: 100, OnlineStatusRaw: 1, SceneId: 10, GroupId: 0,
                FastSync: null, Social: null),
            new(CharId: 2, EnterTimeRaw: 200, OnlineStatusRaw: 1, SceneId: 10, GroupId: 0,
                FastSync: null, Social: null),
        };
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster));
        InvokeDrain(svc);

        Assert.Equal(2, joined.Count);
        Assert.Equal(7777L, svc.PartyId);
        Assert.Equal(1L,    svc.LeaderCharId);
        Assert.Equal(PartyType.Regular5, svc.PartyType);
        Assert.True(svc.IsInParty);
        Assert.True(svc.IsAvailable);
    }

    [Fact]
    public void ApplyFullSnapshot_MemberRemoved_FiresMemberLeft()
    {
        var svc = NewService(out _, out _);

        var roster1 = new List<PartyMemberRoster>
        {
            new(1, 100, 1, 10, 0, null, null),
            new(2, 200, 1, 10, 0, null, null),
        };
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster1));
        InvokeDrain(svc);

        var left = new List<PartyMember>();
        svc.MemberLeft += (m, _) => left.Add(m);

        var roster2 = new List<PartyMemberRoster>
        {
            new(1, 100, 1, 10, 0, null, null),
        };
        // authoritative: true — this is a complete roster (GetTeamInfoReply-style); prune members absent from it.
        // Post-1f8d387 PartyService only prunes on authoritative, non-empty snapshots so that incremental
        // push notifications (which carry partial rosters) never wipe the party.
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster2), authoritative: true);
        InvokeDrain(svc);

        Assert.Single(left);
        Assert.Equal(2L, left[0].CharId);
    }

    [Fact]
    public void ApplyFullSnapshot_EmptyRoster_DoesNotPruneMembers()
    {
        var svc = NewService(out _, out _);
        var roster = new List<PartyMemberRoster>
        {
            new(1, 100, 1, 10, 0, null, null),
            new(2, 200, 1, 10, 0, null, null),
        };
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster));
        InvokeDrain(svc);
        Assert.Equal(2, svc.Members.Count);

        var left = new List<PartyMember>();
        svc.MemberLeft += (m, _) => left.Add(m);

        // A periodic NoticeUpdateTeamInfo carries team metadata with an EMPTY roster
        // (observed in-game: members=0). It must NOT prune the party — real
        // departures arrive via MemberLeft / Dissolve. Previously this wiped everyone.
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false,
            new List<PartyMemberRoster>()));
        InvokeDrain(svc);

        Assert.Empty(left);                  // nobody pruned by the empty snapshot
        Assert.Equal(2, svc.Members.Count);  // roster preserved
    }

    [Fact]
    public void ApplyMemberFastSync_HpChange_FiresMemberUpdated()
    {
        var svc = NewService(out _, out _);

        var roster = new List<PartyMemberRoster>
        {
            new(1, 100, 1, 10, 0, null, null),
        };
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster));
        InvokeDrain(svc);

        var updates = new List<PartyMember>();
        svc.MemberUpdated += m => updates.Add(m);

        svc.EnqueueMemberFastSync(1, new PartyMemberFastSync(10, Position3D.Zero, 5000, 10000, 0));
        InvokeDrain(svc);

        Assert.Single(updates);
        Assert.Equal(5000L,  updates[0].Hp);
        Assert.Equal(10000L, updates[0].MaxHp);
    }

    [Fact]
    public void ApplyMemberFastSync_BeforeKnownRoster_CreatesSlotLazily()
    {
        var svc = NewService(out _, out _);
        var joins = new List<PartyMember>();
        svc.MemberJoined += m => joins.Add(m);

        svc.EnqueueMemberFastSync(42, new PartyMemberFastSync(10, Position3D.Zero, 100, 200, 0));
        InvokeDrain(svc);

        Assert.Single(joins);
        Assert.Equal(42L, joins[0].CharId);
    }

    [Fact]
    public void ApplyMemberSocialSync_NameAndGroup_FiresMemberUpdated()
    {
        var svc = NewService(out _, out _);

        var roster = new List<PartyMemberRoster>
        {
            new(1, 100, 1, 10, 0, null, null),
        };
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster));
        InvokeDrain(svc);

        var updates = new List<PartyMember>();
        svc.MemberUpdated += m => updates.Add(m);

        svc.EnqueueMemberSocialSync(1, new PartyMemberSocialSync("Bob", 50, 7, 3));
        InvokeDrain(svc);

        Assert.Single(updates);
        Assert.Equal("Bob", updates[0].Name);
        Assert.Equal(50,    updates[0].Level);
        Assert.Equal(7,     updates[0].Profession);
        Assert.Equal(3,     updates[0].GroupId);
    }

    [Fact]
    public void ApplyMemberLeft_RemovesSlotAndFiresLeftWithKind()
    {
        var svc = NewService(out _, out _);
        var roster = new List<PartyMemberRoster>
        {
            new(1, 100, 1, 10, 0, null, null),
            new(2, 200, 1, 10, 0, null, null),
        };
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster));
        InvokeDrain(svc);

        var leaves = new List<(PartyMember, PartyLeaveKind)>();
        svc.MemberLeft += (m, k) => leaves.Add((m, k));

        svc.EnqueueMemberLeft(2, 2);   // raw leave_type = 2 → Kicked
        InvokeDrain(svc);

        Assert.Single(leaves);
        Assert.Equal(2L, leaves[0].Item1.CharId);
        Assert.Equal(PartyLeaveKind.Kicked, leaves[0].Item2);
        Assert.Single(svc.Members);
    }

    [Fact]
    public void ApplyDissolve_ClearsAllStateAndFiresPartyDissolved()
    {
        var svc = NewService(out _, out _);
        var roster = new List<PartyMemberRoster>
        {
            new(1, 100, 1, 10, 0, null, null),
            new(2, 200, 1, 10, 0, null, null),
        };
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster));
        InvokeDrain(svc);

        int dissolveCalls = 0;
        svc.PartyDissolved += () => dissolveCalls++;

        svc.EnqueueDissolve();
        InvokeDrain(svc);

        Assert.Equal(1, dissolveCalls);
        Assert.Empty(svc.Members);
        Assert.Equal(0L, svc.PartyId);
        Assert.False(svc.IsInParty);
    }

    // OnCombatEvent_DamageBySelf_AccumulatesDps removed — DPS aggregation moved
    // from PartyService into CombatService (keyed by source EntityId, not by
    // party-member charId). The equivalent coverage now belongs in CombatService
    // tests; see ICombatLookup.GetLiveDps and CombatService.AccumulateDps.

    // --- Task B2 (C-12): Drain buffer reuse across multiple cycles ---

    [Fact]
    public void Drain_MultipleCycles_EventsDispatchedCorrectlyEachCycle()
    {
        // Drives 3 Drain cycles in sequence. Asserts that events dispatched in
        // each cycle are correct and that no cross-cycle contamination occurs
        // (i.e., buffer reuse cannot corrupt state from a previous cycle).
        var svc = NewService(out _, out _);
        var joins  = new List<PartyMember>();
        var leaves = new List<PartyMember>();
        svc.MemberJoined += m => joins.Add(m);
        svc.MemberLeft   += (m, _) => leaves.Add(m);

        // Cycle 1: full snapshot with 2 members → 2 joins.
        var roster1 = new List<PartyMemberRoster>
        {
            new(1, 100, 1, 10, 0, null, null),
            new(2, 200, 1, 10, 0, null, null),
        };
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster1));
        InvokeDrain(svc);
        Assert.Equal(2, joins.Count);
        Assert.Empty(leaves);

        // Cycle 2: member 2 leaves → 1 leave, no additional joins.
        joins.Clear();
        svc.EnqueueMemberLeft(2, 1);
        InvokeDrain(svc);
        Assert.Empty(joins);
        Assert.Single(leaves);
        Assert.Equal(2L, leaves[0].CharId);

        // Cycle 3: new member 3 joins → 1 join, no additional leaves.
        leaves.Clear();
        svc.EnqueueMemberFastSync(3, new PartyMemberFastSync(10, Position3D.Zero, 100, 200, 0));
        InvokeDrain(svc);
        Assert.Single(joins);
        Assert.Equal(3L, joins[0].CharId);
        Assert.Empty(leaves);
    }

    [Fact]
    public void Drain_DedupSemanticsPreserved_MemberJoinedOncePerNewMember()
    {
        // If a charId appears twice in a full snapshot the second occurrence
        // must NOT fire a second MemberJoined — the HashSet dedup semantics
        // (newCharIds) must hold across buffer reuses.
        var svc = NewService(out _, out _);
        var joins = new List<PartyMember>();
        svc.MemberJoined += m => joins.Add(m);

        var roster = new List<PartyMemberRoster>
        {
            new(1, 100, 1, 10, 0, null, null),
            new(2, 200, 1, 10, 0, null, null),
        };
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster));
        InvokeDrain(svc);
        Assert.Equal(2, joins.Count);

        // Same roster again — no new joins (members already exist in slots).
        joins.Clear();
        svc.EnqueueFullSnapshot(new PartyWireSnapshot(7777, 1, PartyType.Regular5, false, roster));
        InvokeDrain(svc);
        Assert.Empty(joins); // existing members → no joins, at most MemberUpdated if state changed
    }

    internal static void InvokeDrain(PartyService svc)
    {
        var m = typeof(PartyService).GetMethod("Drain", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        m!.Invoke(svc, null);
    }
}
