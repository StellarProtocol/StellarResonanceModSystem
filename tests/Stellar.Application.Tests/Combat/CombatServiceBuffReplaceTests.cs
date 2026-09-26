using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

// Cross-thread pins deliberately block on a bounded Task.Wait: the point is to prove a lock is (or is not)
// held by another thread, which an async test cannot express deterministically.
#pragma warning disable xUnit1031

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// Pins the Appear buff SEED (spec-from-talent-buffs, owner go 2026-09-26; review fix round A/G/H 2026-09-26):
/// an AOI appear (or EnterScene self) carries the entity's FULL buff set, so
/// <see cref="CombatService.ReplaceEntityBuffs"/> replaces the held set SILENTLY and raises exactly ONE
/// <see cref="CombatEvent.EntityBuffsSeeded"/> per entity carrying the complete new set — never a per-buff
/// Applied/Removed flood (a 30-player town crowd × ~120 buffs used to raise ~3,600 events). Live method-45 deltas
/// keep raising per-buff <see cref="CombatEvent.BuffChanged"/> exactly as before.
/// </summary>
public sealed class CombatServiceBuffReplaceTests
{
    private static readonly EntityId Player = new((1248014L << 16) | 640L);

    private static ActiveBuff B(int uuid, int baseId) =>
        new(uuid, baseId, 1, Player, 1, 1, 1000, 0, SourceKind: 6, SourceId: baseId / 10);

    private static (CombatService svc, List<CombatEvent> events) Make()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        var list = new List<CombatEvent>();
        svc.CombatEventOccurred += list.Add;
        return (svc, list);
    }

    private static IEnumerable<T> Of<T>(List<CombatEvent> events) => events.OfType<T>();

    [Fact]
    public void Appear_SeedsBuffs_EmitsOneSeedEvent_NoPerBuffEvents()
    {
        var (svc, events) = Make();

        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        svc.Drain();

        Assert.Equal(new[] { 9001, 9002 }, svc.BuffsFor(Player).Select(b => b.BaseId).OrderBy(x => x));
        Assert.Empty(Of<CombatEvent.BuffChanged>(events));
        var seed = Assert.Single(Of<CombatEvent.EntityBuffsSeeded>(events));
        Assert.Equal(Player, seed.TargetId);
        Assert.Equal(1000, seed.TimestampMs);
        Assert.Equal(new[] { 9001, 9002 }, seed.Buffs.Select(b => b.BaseId).OrderBy(x => x));
    }

    [Fact]
    public void TownCrowd_30Players_x120Buffs_Yields30SeedEvents()
    {
        var (svc, events) = Make();
        for (int p = 0; p < 30; p++)
        {
            var id = new EntityId(((1000L + p) << 16) | 640L);
            var buffs = Enumerable.Range(1, 120).Select(i => B(i, 9000 + i)).ToArray();
            svc.ReplaceEntityBuffs(id, buffs, 1000);
        }
        svc.Drain();

        Assert.Equal(30, Of<CombatEvent.EntityBuffsSeeded>(events).Count());
        Assert.All(Of<CombatEvent.EntityBuffsSeeded>(events), e => Assert.Equal(120, e.Buffs.Count));
        Assert.Empty(Of<CombatEvent.BuffChanged>(events));
    }

    [Fact]
    public void ReAppear_WithDifferentSet_OneSeedEvent_NoRemovedFlood()
    {
        var (svc, events) = Make();
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        svc.Drain();
        events.Clear();

        svc.ReplaceEntityBuffs(Player, new[] { B(2, 9002), B(3, 9003) }, 2000);
        svc.Drain();

        Assert.Empty(Of<CombatEvent.BuffChanged>(events));
        var seed = Assert.Single(Of<CombatEvent.EntityBuffsSeeded>(events));
        Assert.Equal(new[] { 9002, 9003 }, seed.Buffs.Select(b => b.BaseId).OrderBy(x => x));
        Assert.Equal(new[] { 9002, 9003 }, svc.BuffsFor(Player).Select(b => b.BaseId).OrderBy(x => x));
    }

    [Fact]
    public void Appear_EmptyOrNull_StillSeeds_SoConsumersCanClear()
    {
        var (svc, events) = Make();
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001) }, 1000);
        svc.Drain();
        events.Clear();

        svc.ReplaceEntityBuffs(Player, null, 2000);
        svc.Drain();

        var seed = Assert.Single(Of<CombatEvent.EntityBuffsSeeded>(events));
        Assert.Empty(seed.Buffs);
        Assert.Empty(Of<CombatEvent.BuffChanged>(events));
        Assert.Empty(svc.BuffsFor(Player));
    }

    [Fact]
    public void Disappear_ThenReAppear_NeverKeepsStaleBuffs()
    {
        var (svc, _) = Make();
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
    public void EnterSceneSeed_BeforeSetLocalEntityId_SurfacesInLocalBuffs()
    {
        // m5: EnterScene's PlayerEnt buffs land before SyncToMe tells us self's id.
        var (svc, _) = Make();
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        Assert.Empty(svc.LocalBuffs);

        svc.SetLocalEntityId(Player);

        Assert.Equal(new[] { 9001, 9002 }, svc.LocalBuffs.Select(b => b.BaseId).OrderBy(x => x));
    }

    [Fact]
    public void DeltaAfterSeed_EmitsPerBuffEventsAsBefore()
    {
        // The method-45 delta path keeps working on top of the seed: a removal by buff_uuid only
        // (no base id on the wire) finds the seeded entry, and an add raises Applied.
        var (svc, events) = Make();
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        svc.Drain();
        events.Clear();

        svc.ApplyBuffEvents(Player, new[] { B(3, 9003) }, new[] { 1 }, 2000);
        svc.Drain();

        var changes = Of<CombatEvent.BuffChanged>(events).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, e => e.Kind == BuffChangeKind.Removed && e.BaseId == 9001);
        Assert.Contains(changes, e => e.Kind == BuffChangeKind.Applied && e.BaseId == 9003);
        Assert.Empty(Of<CombatEvent.EntityBuffsSeeded>(events));
    }

    [Fact]
    public void BuffsFor_ReturnsCachedSnapshot_UntilTheSetChanges()
    {
        // perf G: the per-entity snapshot is built once per change, not once per read.
        var (svc, _) = Make();
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);

        var first = svc.BuffsFor(Player);
        Assert.Same(first, svc.BuffsFor(Player));

        svc.ApplyBuffEvents(Player, new[] { B(3, 9003) }, System.Array.Empty<int>(), 2000);
        var second = svc.BuffsFor(Player);
        Assert.NotSame(first, second);
        Assert.Equal(3, second.Count);
        Assert.Equal(2, first.Count);   // a handed-out snapshot never mutates under the caller
    }

    // ---- final review round (2026-09-26) ------------------------------------------------------------------

    [Fact]
    public void Snapshots_AreReadOnly_AndSharedAcrossBuffsForLocalBuffsAndSeedPayload()
    {
        // m3: BuffsFor, LocalBuffs and EntityBuffsSeeded.Buffs hand out ONE cached read-only wrapper per change —
        // no consumer can cast it back to a mutable array and corrupt what the others see.
        var (svc, events) = Make();
        svc.SetLocalEntityId(Player);
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001), B(2, 9002) }, 1000);
        svc.Drain();

        var viaLookup = svc.BuffsFor(Player);
        var seedPayload = Assert.Single(Of<CombatEvent.EntityBuffsSeeded>(events)).Buffs;
        Assert.Same(viaLookup, seedPayload);
        Assert.Same(viaLookup, svc.LocalBuffs);
        Assert.IsNotType<ActiveBuff[]>(viaLookup);
        Assert.Null(viaLookup as ActiveBuff[]);
        Assert.Throws<System.NotSupportedException>(() => ((IList<ActiveBuff>)viaLookup)[0] = default);
        Assert.Throws<System.NotSupportedException>(() => ((IList<ActiveBuff>)viaLookup).Clear());
    }

    [Fact]
    public void MobAppear_NothingHeld_EmptySnapshot_RaisesNoSeedEvent()
    {
        // n1: a mob/NPC appearing with no buffs (nothing held for it) has nothing to seed or clear.
        var (svc, events) = Make();
        var mob = new EntityId((77L << 16) | 64L);

        svc.ReplaceEntityBuffs(mob, null, 1000);
        svc.ReplaceEntityBuffs(mob, System.Array.Empty<ActiveBuff>(), 1100);
        svc.Drain();

        Assert.Empty(events);
        Assert.Empty(svc.BuffsFor(mob));
    }

    [Fact]
    public void LocalBuffs_ReadNeverWaitsOnTheBuffLock()
    {
        // n2: LocalBuffs is read every frame on the main thread; the network thread holds the buff lock while it
        // applies deltas (and, with STELLAR_DIAGNOSTICS=1, while it logs every buff). The read must be a volatile
        // snapshot reference, not a lock acquisition. Deterministic: hold the lock on another thread for the
        // whole read.
        var (svc, _) = Make();
        svc.SetLocalEntityId(Player);
        svc.ReplaceEntityBuffs(Player, new[] { B(1, 9001) }, 1000);
        var gate = typeof(CombatService).GetField("_buffsByEntityLock",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(svc)!;

        using var held = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        var holder = new System.Threading.Thread(() =>
        {
            lock (gate) { held.Set(); release.Wait(); }
        });
        holder.Start();
        held.Wait();
        try
        {
            var read = System.Threading.Tasks.Task.Run(() => svc.LocalBuffs.Count);
            Assert.True(read.Wait(System.TimeSpan.FromSeconds(5)), "LocalBuffs blocked on the buff lock");
            Assert.Equal(1, read.Result);
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    [Fact]
    public void LocalBuffs_TracksDeltasRemovalsAndClear()
    {
        var (svc, _) = Make();
        svc.SetLocalEntityId(Player);
        svc.ApplyBuffEvents(Player, new[] { B(1, 9001), B(2, 9002) }, System.Array.Empty<int>(), 1000);
        Assert.Equal(2, svc.LocalBuffs.Count);
        svc.ApplyBuffEvents(Player, System.Array.Empty<ActiveBuff>(), new[] { 1 }, 2000);
        Assert.Equal(9002, Assert.Single(svc.LocalBuffs).BaseId);
        svc.OnEntityDisappeared(Player, EntityDisappearReason.Normal);
        Assert.Empty(svc.LocalBuffs);
        svc.ApplyBuffEvents(Player, new[] { B(3, 9003) }, System.Array.Empty<int>(), 3000);
        Assert.Single(svc.LocalBuffs);
        svc.ClearAllBuffs();
        Assert.Empty(svc.LocalBuffs);
    }
}
