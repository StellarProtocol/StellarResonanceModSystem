using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// Pins the talent-spec lifecycle fixes from the 2026-09-26 qa/perf review round (owner "go"):
/// B (M1) a talent spec never goes stale — a rootless seed clears it, a root-removal gap expires after 10 s,
/// Disappear stops it being authoritative; C (m3) only a <c>SourceKind == 6</c> (Talent) buff counts as a root;
/// E (m1) one SpecChanged per packet even if a drain lands mid-packet; F (m2) a reset between collecting and
/// publishing drops the batch; H (m5) no SpecChanged on scene reset or for monsters.
/// </summary>
public sealed class TalentSpecLifecycleTests
{
    private const int AttrProfessionId = 220;
    private const int SmiteRoot = 2202110, LifebindRoot = 2202340, FalconryRoot = 2203290, ConcertoRoot = 2207180;
    private static readonly EntityId P   = new((4242L << 16) | 640L);
    private static readonly EntityId Mob = new((77L << 16) | 64L);
    private static readonly int[] None = System.Array.Empty<int>();

    private static ActiveBuff Talent(int uuid, int baseId, int sourceKind = 6) =>
        new(uuid, baseId, 1, P, 1, 1, 1000, 0, SourceKind: sourceKind, SourceId: 1);

    private static CombatEvent.DamageDealt Hit(EntityId src, int skillId) =>
        new(1000, src, Mob, skillId, 100, 100, 0, false, false, false, false, default, default);

    private sealed class Clock { public long Now = 1_000_000; }

    private static (CombatService svc, List<CombatEvent.SpecChanged> ev, Clock clock) Make()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        var clock = new Clock();
        svc.SpecClock = () => clock.Now;
        var list = new List<CombatEvent.SpecChanged>();
        svc.CombatEventOccurred += e => { if (e is CombatEvent.SpecChanged sc) list.Add(sc); };
        return (svc, list, clock);
    }

    private static (int, int, bool)[] Triples(List<CombatEvent.SpecChanged> ev) =>
        ev.Select(e => (e.OldSubProfessionId, e.NewSubProfessionId, e.FromTalent)).ToArray();

    // ---- B: stale authoritative spec -------------------------------------------------------------

    [Fact]
    public void B_SeedWithoutRoot_ClearsTalentSpec()
    {
        var (svc, ev, _) = Make();
        svc.SetEntityAttribute(P, AttrProfessionId, 5);
        svc.ReplaceEntityBuffs(P, new[] { Talent(1, SmiteRoot) }, 1000);
        svc.Drain();
        ev.Clear();

        svc.ReplaceEntityBuffs(P, new[] { Talent(2, 9001) }, 2000);   // full snapshot, no root
        svc.Drain();

        Assert.Equal(0, svc.GetSubProfession(P));
        Assert.False(svc.TryGetTalentSpec(P, out _));
        Assert.Equal(new[] { (50001, 0, false) }, Triples(ev));
    }

    [Fact]
    public void B_RootRemovedGap_ExpiresAfter10s_FallsBackAndFires()
    {
        var (svc, ev, clock) = Make();
        svc.SetEntityAttribute(P, AttrProfessionId, 5);
        svc.ApplyBuffEvents(P, new[] { Talent(1, SmiteRoot) }, None, 1000);
        svc.Drain();
        ev.Clear();

        svc.ApplyBuffEvents(P, System.Array.Empty<ActiveBuff>(), new[] { 1 }, 2000);   // gap starts
        clock.Now += 10_000;                                                             // exactly 10 s: still held
        svc.Drain();
        Assert.Equal(50001, svc.GetSubProfession(P));
        Assert.True(svc.TryGetTalentSpec(P, out _));
        Assert.Empty(ev);

        clock.Now += 1;                                                                  // past 10 s: dropped
        svc.Drain();
        Assert.Equal(0, svc.GetSubProfession(P));
        Assert.False(svc.TryGetTalentSpec(P, out _));
        Assert.Equal(new[] { (50001, 0, false) }, Triples(ev));
    }

    [Fact]
    public void B_GapExpiry_FallsBackToCastOfSameClass()
    {
        var (svc, ev, clock) = Make();
        svc.SetEntityAttribute(P, AttrProfessionId, 5);
        svc.ApplyBuffEvents(P, new[] { Talent(1, LifebindRoot) }, None, 1000);
        svc.EnqueueEvent(Hit(P, 1518));   // Smite cast recorded, loses to the root
        svc.Drain();
        ev.Clear();

        svc.ApplyBuffEvents(P, System.Array.Empty<ActiveBuff>(), new[] { 1 }, 2000);
        clock.Now += 10_001;
        svc.Drain();

        Assert.Equal(50001, svc.GetSubProfession(P));
        Assert.False(svc.TryGetTalentSpec(P, out _));
        Assert.Equal(new[] { (50002, 50001, false) }, Triples(ev));
    }

    [Fact]
    public void B_NewRootInsideGap_CancelsExpiry()
    {
        var (svc, ev, clock) = Make();
        svc.SetEntityAttribute(P, AttrProfessionId, 5);
        svc.ApplyBuffEvents(P, new[] { Talent(1, SmiteRoot) }, None, 1000);
        svc.Drain();
        svc.ApplyBuffEvents(P, System.Array.Empty<ActiveBuff>(), new[] { 1 }, 2000);
        clock.Now += 2_000;
        svc.ApplyBuffEvents(P, new[] { Talent(2, LifebindRoot) }, None, 4000);
        clock.Now += 60_000;
        svc.Drain();

        Assert.Equal(50002, svc.GetSubProfession(P));
        Assert.True(svc.TryGetTalentSpec(P, out _));
        Assert.Equal(new[] { (0, 50001, true), (50001, 50002, true) }, Triples(ev));
    }

    [Fact]
    public void B_Disappear_NotAuthoritative_KeepsLastValue_UntilSeedOrReset()
    {
        var (svc, ev, _) = Make();
        svc.SetEntityAttribute(P, AttrProfessionId, 5);
        svc.ReplaceEntityBuffs(P, new[] { Talent(1, SmiteRoot) }, 1000);
        svc.Drain();
        ev.Clear();

        svc.OnEntityDisappeared(P, EntityDisappearReason.Normal);
        svc.Drain();
        Assert.False(svc.TryGetTalentSpec(P, out _));
        Assert.Equal(50001, svc.GetSubProfession(P));   // last value kept, so no SpecChanged
        Assert.Empty(ev);

        svc.ReplaceEntityBuffs(P, new[] { Talent(2, LifebindRoot) }, 5000);   // re-appear seed
        svc.Drain();
        Assert.True(svc.TryGetTalentSpec(P, out var spec));
        Assert.Equal(50002, spec);
        Assert.Equal(new[] { (50001, 50002, true) }, Triples(ev));

        svc.ResetEntities();
        Assert.Equal(0, svc.GetSubProfession(P));
    }

    // ---- C: only Talent-sourced buffs are roots ---------------------------------------------------

    [Fact]
    public void C_RootIdWithNonTalentSource_IsIgnored()
    {
        var (svc, ev, _) = Make();
        svc.ApplyBuffEvents(P, new[] { Talent(1, SmiteRoot, sourceKind: 0) }, None, 1000);
        svc.ReplaceEntityBuffs(P, new[] { Talent(2, LifebindRoot, sourceKind: 10) }, 2000);
        svc.Drain();

        Assert.Equal(0, svc.GetSubProfession(P));
        Assert.False(svc.TryGetTalentSpec(P, out _));
        Assert.Empty(ev);
    }

    [Fact]
    public void C_RootIdWithTalentSource_Counts()
    {
        var (svc, _, _) = Make();
        svc.ApplyBuffEvents(P, new[] { Talent(1, SmiteRoot, sourceKind: 6) }, None, 1000);
        Assert.Equal(50001, svc.GetSubProfession(P));
    }

    // ---- E: one SpecChanged per packet --------------------------------------------------------------

    [Fact]
    public void E_ClassSwapPacket_WithDrainMidPacket_YieldsExactlyOneEvent()
    {
        var (svc, ev, _) = Make();
        svc.SetEntityAttribute(P, AttrProfessionId, 11);
        svc.ApplyBuffEvents(P, new[] { Talent(1, FalconryRoot) }, None, 1000);
        svc.Drain();
        ev.Clear();

        svc.BeginPacket();
        svc.SetEntityAttribute(P, AttrProfessionId, 13);   // attr 220 lands first …
        svc.Drain();                                        // … a drain on another thread sees half a packet
        Assert.Empty(ev);
        svc.ApplyBuffEvents(P, new[] { Talent(2, ConcertoRoot) }, new[] { 1 }, 2000);
        svc.EndPacket();
        svc.Drain();

        Assert.Equal(new[] { (110002, 130002, true) }, Triples(ev));
    }

    // ---- F: reset generation --------------------------------------------------------------------------

    [Fact]
    public void F_ResetBetweenTakeAndPublish_DropsTheBatch()
    {
        var tracker = new CombatEntityTracker();
        var resolver = new TalentSpecResolver(new SpecRootBuffMap(new StubLog()), tracker);
        resolver.NoteBuff(P, SmiteRoot, 6, 1000);

        var batch = resolver.TakeDirty();                   // taken in the OLD scene (generation g)
        resolver.Reset();                                   // scene change lands between the two halves
        resolver.NoteBuff(P, SmiteRoot, 6, 2000);           // the new scene already knows P again

        Assert.Null(resolver.Publish(batch));               // stale batch dropped — it must not report into
                                                            // the new scene with the old scene's timestamp
        var changes = resolver.Publish(resolver.TakeDirty());
        var c = Assert.Single(changes!);                    // the new scene reports P once, from a clean slate
        Assert.Equal((0, 50001, true, 2000L), (c.OldSubProfessionId, c.NewSubProfessionId, c.FromTalent, c.TimestampMs));
    }

    // ---- H: no SpecChanged on scene reset or for monsters ------------------------------------------------

    [Fact]
    public void H_SceneReset_RaisesNoSpecChanged()
    {
        var (svc, ev, _) = Make();
        svc.ApplyBuffEvents(P, new[] { Talent(1, SmiteRoot) }, None, 1000);
        svc.Drain();
        ev.Clear();

        svc.ResetEntities();
        svc.Drain();

        Assert.Empty(ev);
    }

    [Fact]
    public void H_Monster_NeverGetsASpec()
    {
        var (svc, ev, _) = Make();
        svc.ApplyBuffEvents(Mob, new[] { Talent(1, SmiteRoot) }, None, 1000);
        svc.ReplaceEntityBuffs(Mob, new[] { Talent(2, LifebindRoot) }, 1500);
        svc.EnqueueEvent(Hit(Mob, 1518));
        svc.Drain();

        Assert.Equal(0, svc.GetSubProfession(Mob));
        Assert.False(svc.TryGetTalentSpec(Mob, out _));
        Assert.Empty(ev);
    }
}
