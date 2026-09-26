using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// Pins the Appear buff-seed REPLACE semantics (spec-from-talent-buffs, owner go 2026-09-26):
/// an AOI appear (or EnterScene self) carries the entity's FULL buff set, so
/// <see cref="CombatService.ReplaceEntityBuffs"/> emits Applied for new uuids, Removed for uuids no longer
/// present and nothing for unchanged ones — and a re-appear after a Disappear never keeps stale buffs.
/// </summary>
public sealed class CombatServiceBuffReplaceTests
{
    private static readonly EntityId Player = new((1248014L << 16) | 640L);

    private static ActiveBuff B(int uuid, int baseId) =>
        new(uuid, baseId, 1, Player, 1, 1, 1000, 0, SourceKind: 6, SourceId: baseId / 10);

    private static (CombatService svc, List<CombatEvent.BuffChanged> buffEvents) Make()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        var list = new List<CombatEvent.BuffChanged>();
        svc.CombatEventOccurred += e => { if (e is CombatEvent.BuffChanged bc) list.Add(bc); };
        return (svc, list);
    }

    [Fact]
    public void Appear_SeedsBuffs_EmitsAppliedForEach()
    {
        var (svc, events) = Make();

        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        svc.Drain();

        Assert.Equal(new[] { 9001, 9002 }, svc.BuffsFor(Player).Select(b => b.BaseId).OrderBy(x => x));
        Assert.All(events, e => Assert.Equal(BuffChangeKind.Applied, e.Kind));
        Assert.Equal(new[] { 1, 2 }, events.Select(e => e.BuffUuid).OrderBy(x => x));
        Assert.All(events, e => Assert.Equal(6, e.SourceKind));
    }

    [Fact]
    public void ReAppear_WithDifferentSet_RemovesAndAddsExactly()
    {
        var (svc, events) = Make();
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        svc.Drain();
        events.Clear();

        svc.ReplaceEntityBuffs(Player, new[] { B(2, 9002), B(3, 9003) }, 2000);
        svc.Drain();

        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.Kind == BuffChangeKind.Removed && e.BuffUuid == 1 && e.BaseId == 9001);
        Assert.Contains(events, e => e.Kind == BuffChangeKind.Applied && e.BuffUuid == 3 && e.BaseId == 9003);
        Assert.Equal(new[] { 9002, 9003 }, svc.BuffsFor(Player).Select(b => b.BaseId).OrderBy(x => x));
    }

    [Fact]
    public void ReAppear_Unchanged_EmitsNothing()
    {
        var (svc, events) = Make();
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        svc.Drain();
        events.Clear();

        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 2000);
        svc.Drain();

        Assert.Empty(events);
    }

    [Fact]
    public void Appear_EmptyOrNull_ClearsExistingSet()
    {
        var (svc, events) = Make();
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001) }, 1000);
        svc.Drain();
        events.Clear();

        svc.ReplaceEntityBuffs(Player, null, 2000);
        svc.Drain();

        var removed = Assert.Single(events);
        Assert.Equal(BuffChangeKind.Removed, removed.Kind);
        Assert.Empty(svc.BuffsFor(Player));
    }

    [Fact]
    public void Disappear_ThenReAppear_NeverKeepsStaleBuffs()
    {
        var (svc, events) = Make();
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        svc.OnEntityDisappeared(Player, EntityDisappearReason.Normal);
        Assert.Empty(svc.BuffsFor(Player));   // Disappear drops the set (no events — unchanged behaviour)

        svc.ReplaceEntityBuffs(Player, new[] { B(7, 9007) }, 3000);

        Assert.Equal(new[] { 9007 }, svc.BuffsFor(Player).Select(b => b.BaseId));
    }

    [Fact]
    public void Appear_LocalEntity_RefreshesLocalBuffs()
    {
        var (svc, _) = Make();
        svc.SetLocalEntityId(Player);

        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        Assert.Equal(2, svc.LocalBuffs.Count);

        svc.ReplaceEntityBuffs(Player, new[] { B(2, 9002) }, 2000);
        Assert.Equal(9002, Assert.Single(svc.LocalBuffs).BaseId);
    }

    [Fact]
    public void Appear_ThenDeltaEvents_ComposeOnTheSeededSet()
    {
        // The method-45 delta path keeps working on top of the seed: a removal by buff_uuid only
        // (no base id on the wire) finds the seeded entry.
        var (svc, events) = Make();
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        svc.Drain();
        events.Clear();

        svc.ApplyBuffEvents(Player, System.Array.Empty<ActiveBuff>(), new[] { 1 }, 2000);
        svc.Drain();

        var removed = Assert.Single(events);
        Assert.Equal(9001, removed.BaseId);
    }
}
