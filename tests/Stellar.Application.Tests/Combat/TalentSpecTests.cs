using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// Pins buff-first spec resolution (spec-from-talent-buffs, owner go 2026-09-26; evidence
/// docs/recon/spec-from-public-wire-data.md). Each of the 18 specs has ONE root talent ("&lt;Spec&gt; Spec")
/// granting ONE spec-exclusive buff; when present it is AUTHORITATIVE over cast inference. The Miyuki
/// sequence is the owner-confirmed live swap from capture <c>stellar-wirecap-20260926-164712</c>:
/// Marksman/Falconry → Beat Performer/Concerto → Verdant Oracle/Smite → ~2 s gap → Lifebind.
/// </summary>
public sealed class TalentSpecTests
{
    private const int AttrProfessionId = 220;
    private static readonly EntityId Miyuki = new((4242L << 16) | 640L);
    private static readonly EntityId Caster = new((5151L << 16) | 640L);
    private static readonly EntityId Mob    = new((77L << 16) | 64L);

    // Root buffs (release_3.7 TalentTable effect [3, buffId, 1] of each "<Spec> Spec" talent).
    private const int FalconryRoot = 2203290, ConcertoRoot = 2207180, SmiteRoot = 2202110, LifebindRoot = 2202340;

    private static ActiveBuff Talent(int uuid, int baseId) =>
        new(uuid, baseId, 1, Miyuki, 1, 1, 1000, 0, SourceKind: 6, SourceId: 1);

    private static CombatEvent.DamageDealt Hit(EntityId src, int skillId) =>
        new(1000, src, Mob, skillId, 100, 100, 0, false, false, false, false, default, default);

    private static (CombatService svc, List<CombatEvent.SpecChanged> specEvents) Make()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        var list = new List<CombatEvent.SpecChanged>();
        svc.CombatEventOccurred += e => { if (e is CombatEvent.SpecChanged sc) list.Add(sc); };
        return (svc, list);
    }

    private static readonly int[] None = System.Array.Empty<int>();

    [Fact]
    public void RootPresent_ResolvesSpec_AndIsTalentAuthoritative()
    {
        var (svc, events) = Make();
        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 5);
        svc.ReplaceEntityBuffs(Miyuki, new[] { Talent(1, SmiteRoot), Talent(2, 2202111) }, 1000);
        svc.Drain();

        Assert.Equal(50001, svc.GetSubProfession(Miyuki));
        Assert.True(svc.TryGetTalentSpec(Miyuki, out var spec));
        Assert.Equal(50001, spec);
        var ev = Assert.Single(events);
        Assert.Equal((0, 50001, true), (ev.OldSubProfessionId, ev.NewSubProfessionId, ev.FromTalent));
        Assert.Equal(Miyuki, ev.TargetId);
    }

    [Fact]
    public void MiyukiSequence_ResolvesEveryStep_GapKeepsSmite_ExactEventList()
    {
        var (svc, events) = Make();

        // 1. Marksman / Falconry (appear).
        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 11);
        svc.ReplaceEntityBuffs(Miyuki, new[] { Talent(1, FalconryRoot), Talent(2, 2203291) }, 1000);
        svc.Drain();
        Assert.Equal(110002, svc.GetSubProfession(Miyuki));

        // 2. Class swap → Beat Performer / Concerto: attr 220 and the whole talent set in ONE delta.
        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 13);
        svc.ApplyBuffEvents(Miyuki, new[] { Talent(3, ConcertoRoot), Talent(4, 2207181) }, new[] { 1, 2 }, 2000);
        svc.Drain();
        Assert.Equal(130002, svc.GetSubProfession(Miyuki));

        // 3. Class swap → Verdant Oracle / Smite.
        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 5);
        svc.ApplyBuffEvents(Miyuki, new[] { Talent(5, SmiteRoot), Talent(6, 2202111) }, new[] { 3, 4 }, 3000);
        svc.Drain();
        Assert.Equal(50001, svc.GetSubProfession(Miyuki));

        // 4. Same-class swap: Smite tree removed in two deltas — the ~2 s "no spec talents" gap.
        svc.ApplyBuffEvents(Miyuki, System.Array.Empty<ActiveBuff>(), new[] { 5 }, 4000);
        svc.Drain();
        svc.ApplyBuffEvents(Miyuki, System.Array.Empty<ActiveBuff>(), new[] { 6 }, 4100);
        svc.Drain();
        Assert.Equal(50001, svc.GetSubProfession(Miyuki));          // gap KEEPS Smite (no flicker, no 0)
        Assert.True(svc.TryGetTalentSpec(Miyuki, out var gapSpec)); // still talent-derived, same class
        Assert.Equal(50001, gapSpec);

        // 5. Lifebind tree fills node by node; the root arrives in the first delta.
        svc.ApplyBuffEvents(Miyuki, new[] { Talent(7, LifebindRoot) }, None, 6000);
        svc.Drain();
        svc.ApplyBuffEvents(Miyuki, new[] { Talent(8, 2202341) }, None, 6500);
        svc.Drain();
        Assert.Equal(50002, svc.GetSubProfession(Miyuki));

        Assert.Equal(new[]
        {
            (0,      110002, true),
            (110002, 130002, true),
            (130002, 50001,  true),
            (50001,  50002,  true),   // exactly ONE event across the gap
        }, events.Select(e => (e.OldSubProfessionId, e.NewSubProfessionId, e.FromTalent)).ToArray());
    }

    [Fact]
    public void CastOfOtherSpecSignature_WhileRootPresent_DoesNotFlipSpec()
    {
        var (svc, events) = Make();
        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 5);
        svc.ReplaceEntityBuffs(Miyuki, new[] { Talent(1, LifebindRoot) }, 1000);
        svc.Drain();
        events.Clear();

        svc.EnqueueEvent(Hit(Miyuki, 1518));   // Smite signature skill
        svc.Drain();

        Assert.Equal(50002, svc.GetSubProfession(Miyuki));
        Assert.True(svc.TryGetTalentSpec(Miyuki, out var s));
        Assert.Equal(50002, s);
        Assert.Empty(events);
    }

    [Fact]
    public void NoRootEver_CastDerivedUnchanged_NotTalent_EventsFromTalentFalse()
    {
        var (svc, events) = Make();

        svc.EnqueueEvent(Hit(Caster, 1241));    // Frostbeam signature
        svc.Drain();
        Assert.Equal(20002, svc.GetSubProfession(Caster));
        Assert.False(svc.TryGetTalentSpec(Caster, out var none));
        Assert.Equal(0, none);

        svc.EnqueueEvent(Hit(Caster, 120901));  // Icicle signature — last-seen-wins as before
        svc.Drain();
        Assert.Equal(20001, svc.GetSubProfession(Caster));

        Assert.Equal(new[] { (0, 20002, false), (20002, 20001, false) },
            events.Select(e => (e.OldSubProfessionId, e.NewSubProfessionId, e.FromTalent)).ToArray());
    }

    [Fact]
    public void UnknownEntity_NoSpec_NotTalent()
    {
        var (svc, _) = Make();
        Assert.Equal(0, svc.GetSubProfession(Caster));
        Assert.False(svc.TryGetTalentSpec(Caster, out _));
    }

    [Fact]
    public void ClassChange_NoNewRoot_DropsOldClassSpecToZero_FiresEvent()
    {
        var (svc, events) = Make();
        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 13);
        svc.ReplaceEntityBuffs(Miyuki, new[] { Talent(1, ConcertoRoot) }, 1000);
        svc.Drain();
        events.Clear();

        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 5);   // class changed, no Verdant Oracle root yet
        svc.Drain();

        Assert.Equal(0, svc.GetSubProfession(Miyuki));
        Assert.False(svc.TryGetTalentSpec(Miyuki, out _));
        var ev = Assert.Single(events);
        Assert.Equal((130002, 0, false), (ev.OldSubProfessionId, ev.NewSubProfessionId, ev.FromTalent));
    }

    [Fact]
    public void ClassChange_NoNewRoot_FallsBackToCastOfNewClass()
    {
        var (svc, events) = Make();
        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 13);
        svc.ReplaceEntityBuffs(Miyuki, new[] { Talent(1, ConcertoRoot) }, 1000);
        svc.EnqueueEvent(Hit(Miyuki, 1518));   // a Smite cast is recorded but does not win over the root
        svc.Drain();
        Assert.Equal(130002, svc.GetSubProfession(Miyuki));
        events.Clear();

        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 5);
        svc.Drain();

        Assert.Equal(50001, svc.GetSubProfession(Miyuki));
        Assert.False(svc.TryGetTalentSpec(Miyuki, out _));
        var ev = Assert.Single(events);
        Assert.Equal((130002, 50001, false), (ev.OldSubProfessionId, ev.NewSubProfessionId, ev.FromTalent));
    }

    [Fact]
    public void BattleImagineTransformClass_DoesNotInvalidateTalentSpec()
    {
        // Owner ruling 2026-09-25: a Battle Imagine transform (prof 14/15/8) is NOT a class. attr 220
        // flipping to a transform id must not drop the real spec.
        var (svc, events) = Make();
        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 5);
        svc.ReplaceEntityBuffs(Miyuki, new[] { Talent(1, SmiteRoot) }, 1000);
        svc.Drain();
        events.Clear();

        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 14);
        svc.Drain();

        Assert.Equal(50001, svc.GetSubProfession(Miyuki));
        Assert.True(svc.TryGetTalentSpec(Miyuki, out _));
        Assert.Empty(events);
    }

    [Fact]
    public void DuplicateRoot_OnReAppear_ProducesNoSpecEvent()
    {
        var (svc, events) = Make();
        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 5);
        svc.ReplaceEntityBuffs(Miyuki, new[] { Talent(1, SmiteRoot) }, 1000);
        svc.Drain();
        events.Clear();

        svc.OnEntityDisappeared(Miyuki, EntityDisappearReason.Normal);
        svc.ReplaceEntityBuffs(Miyuki, new[] { Talent(9, SmiteRoot) }, 5000);   // new buff uuid, same root
        svc.Drain();

        Assert.Equal(50001, svc.GetSubProfession(Miyuki));
        Assert.Empty(events);
    }

    [Fact]
    public void SceneReset_ClearsTalentSpec()
    {
        var (svc, _) = Make();
        svc.SetEntityAttribute(Miyuki, AttrProfessionId, 5);
        svc.ReplaceEntityBuffs(Miyuki, new[] { Talent(1, SmiteRoot) }, 1000);
        svc.Drain();

        svc.ResetEntities();

        Assert.Equal(0, svc.GetSubProfession(Miyuki));
        Assert.False(svc.TryGetTalentSpec(Miyuki, out _));
    }

    [Fact]
    public void RootOnly_NoClassAttr_StillResolves()
    {
        // attr 220 unknown (e.g. evicted on a non-normal disappear) = no class constraint.
        var (svc, _) = Make();
        svc.ApplyBuffEvents(Miyuki, new[] { Talent(1, FalconryRoot) }, None, 1000);
        Assert.Equal(110002, svc.GetSubProfession(Miyuki));
        Assert.True(svc.TryGetTalentSpec(Miyuki, out _));
    }
}
