using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Combat;

public sealed class CombatServiceTests
{
    [Fact]
    public void IsAvailable_FalseUntilLocalEntityIdSet()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        Assert.False(svc.IsAvailable);
        svc.SetLocalEntityId(new EntityId(0x0000_0001_0000_0280L));
        Assert.True(svc.IsAvailable);
    }

    [Fact]
    public void SetLocalEntityId_IsIdempotent_FirstNonNoneWins()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var first  = new EntityId(0x0000_0001_0000_0280L);
        var second = new EntityId(0x0000_0002_0000_0280L);

        svc.SetLocalEntityId(first);
        svc.SetLocalEntityId(second);

        Assert.Equal(first, svc.LocalEntityId);
    }

    [Fact]
    public void EnqueueEvent_DoesNotFireUntilDrain()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.EnqueueEvent(new CombatEvent.SkillUsed(1000, new EntityId(1), 42, SkillEventPhase.Begin));
        Assert.Empty(fired);

        svc.Drain();
        Assert.Single(fired);
    }

    [Fact]
    public void Drain_PushesIntoRecentEventsRingBuffer()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        for (int i = 0; i < 510; i++)
            svc.EnqueueEvent(new CombatEvent.SkillUsed(i, new EntityId(1), i, SkillEventPhase.Begin));
        svc.Drain();

        Assert.Equal(500, svc.RecentEvents.Count);
        // Oldest 10 evicted.
        Assert.Equal(10,  ((CombatEvent.SkillUsed)svc.RecentEvents.First()).SkillId);
        Assert.Equal(509, ((CombatEvent.SkillUsed)svc.RecentEvents.Last()).SkillId);
    }

    [Fact]
    public void SetServerNowMs_UpdatesProperty()
    {
        // ServerNowMs interpolates: it returns the anchor PLUS local elapsed
        // ticks since SetServerNowMs was called. Immediately after the call,
        // elapsed is 0..a few ms, so the visible value lives in
        // [anchor, anchor + small slack]. Use a tolerant range assertion
        // instead of strict equality.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        const long anchor = 1_700_000_000_000L;
        svc.SetServerNowMs(anchor);
        var read = svc.ServerNowMs;
        Assert.InRange(read, anchor, anchor + 100L);
    }

    [Fact]
    public void ServerNowMs_InterpolatesBetweenAnchorUpdates()
    {
        // The cooldown countdown stuttered (4-5s jumps) because SyncServerTime
        // anchors fire only every ~5s; between anchors, ServerNowMs must
        // advance via local-clock elapsed so CooldownBar reads a smooth clock.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        const long anchor = 5_000L;
        svc.SetServerNowMs(anchor);
        var first = svc.ServerNowMs;
        System.Threading.Thread.Sleep(50);
        var second = svc.ServerNowMs;
        Assert.True(second >= first + 30L,
            $"expected interpolated server clock to advance >= 30ms over a 50ms sleep; first={first} second={second}");
    }

    [Fact]
    public void ServerNowMs_ReturnsZeroBeforeAnyAnchor()
    {
        // Contract: ServerNowMs == 0 means "no server time observed yet" and
        // is the gate LocalCooldowns uses to skip eviction. The interpolation
        // path must preserve that sentinel.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        Assert.Equal(0L, svc.ServerNowMs);
    }

    [Fact]
    public void ServerNowMs_NewAnchorSnapsClockToNewValue()
    {
        // When a fresh SyncServerTime arrives we want the visible clock to
        // jump TO that authoritative value (not continue interpolating from
        // the previous anchor). Verifies that the captured-at tick is reset
        // on every SetServerNowMs.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        svc.SetServerNowMs(10_000L);
        System.Threading.Thread.Sleep(20);
        svc.SetServerNowMs(20_000L);
        var read = svc.ServerNowMs;
        Assert.InRange(read, 20_000L, 20_100L);
    }

    [Fact]
    public void SetLocalCooldowns_SeedsSnapshot()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var cds = new[] { new SkillCooldown(1, 100, 5000, SkillCooldownKind.Normal, 0, 5000) };
        svc.SetLocalCooldowns(cds);
        Assert.Single(svc.LocalCooldowns);
        Assert.Equal(1, svc.LocalCooldowns[0].SkillId);
    }

    [Fact]
    public void SetLocalCooldowns_MergesByteSkillId_PreservesPriorActive()
    {
        // SyncToMeDeltaInfo.SyncSkillCDs is a delta — each tick only contains
        // skills whose cooldown CHANGED. Wholesale replace would empty the bar
        // immediately. Merge semantics keep prior active cooldowns intact.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());

        svc.SetLocalCooldowns(new[] { new SkillCooldown(1, 100, 5000, SkillCooldownKind.Normal, 0, 5000) });
        Assert.Single(svc.LocalCooldowns);

        svc.SetLocalCooldowns(new[] { new SkillCooldown(2, 100, 10000, SkillCooldownKind.Normal, 0, 10000) });
        Assert.Equal(2, svc.LocalCooldowns.Count);

        // Skill 1 refresh — new duration replaces prior, total entry count unchanged.
        svc.SetLocalCooldowns(new[] { new SkillCooldown(1, 200, 8000, SkillCooldownKind.Normal, 0, 8000) });
        Assert.Equal(2, svc.LocalCooldowns.Count);
        var skill1 = svc.LocalCooldowns.First(cd => cd.SkillId == 1);
        Assert.Equal(8000, skill1.DurationMs);
        Assert.Equal(200, skill1.BeginTimeMs);
    }

    [Fact]
    public void LocalCooldowns_EvictsEntriesExpiredOverOneSecond_WhenServerTimeKnown()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        // Seed two cooldowns at server-time 0.
        svc.SetLocalCooldowns(new[]
        {
            new SkillCooldown(1, 0, 5_000, SkillCooldownKind.Normal, 0, 5_000),
            new SkillCooldown(2, 0, 10_000, SkillCooldownKind.Normal, 0, 10_000),
        });

        // Advance server clock past skill 1's end (5s) + 1s grace window.
        svc.SetServerNowMs(6_001);

        // Reading LocalCooldowns triggers eviction of skill 1 (ended > 1s ago).
        var snap = svc.LocalCooldowns;
        Assert.Single(snap);
        Assert.Equal(2, snap[0].SkillId);
    }

    [Fact]
    public void ApplyBuffEvents_LocalEntity_PopulatesLocalBuffs()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var local = new EntityId(0x0000_0001_0000_0280L);
        svc.SetLocalEntityId(local);
        Assert.Empty(svc.LocalBuffs);

        var upserts = new[]
        {
            new ActiveBuff(BuffUuid: 100, BaseId: 9001, Level: 1, FirerId: EntityId.None,
                           Stacks: 1, Layer: 1, CreateTimeMs: 1000, DurationMs: 5000),
            new ActiveBuff(BuffUuid: 101, BaseId: 2110056, Level: 1, FirerId: EntityId.None,
                           Stacks: 1, Layer: 1, CreateTimeMs: 1000, DurationMs: 6000),
        };
        svc.ApplyBuffEvents(local, upserts, System.Array.Empty<int>(), 1000);

        Assert.Equal(2, svc.LocalBuffs.Count);
        Assert.Contains(svc.LocalBuffs, b => b.BaseId == 2110056);
    }

    [Fact]
    public void ApplyBuffEvents_NewBuff_EmitsApplied()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        var entityId = new EntityId(1234L);
        var buff = new ActiveBuff(100, 9001, 1, EntityId.None, 1, 1, 1000, 5000);
        svc.ApplyBuffEvents(entityId, new[] { buff }, System.Array.Empty<int>(), 1000);

        svc.Drain();
        var bc = Assert.IsType<CombatEvent.BuffChanged>(Assert.Single(fired));
        Assert.Equal(BuffChangeKind.Applied, bc.Kind);
        Assert.Equal(100, bc.BuffUuid);
    }

    [Fact]
    public void ApplyBuffEvents_Remove_EmitsRemoved_AndDropsFromSet()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var entityId = new EntityId(1234L);
        var buff = new ActiveBuff(100, 9001, 1, EntityId.None, 1, 1, 1000, 5000);
        svc.ApplyBuffEvents(entityId, new[] { buff }, System.Array.Empty<int>(), 1000);

        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;
        svc.Drain();
        fired.Clear();

        svc.ApplyBuffEvents(entityId, System.Array.Empty<ActiveBuff>(), new[] { 100 }, 6000);
        svc.Drain();

        var bc = Assert.IsType<CombatEvent.BuffChanged>(Assert.Single(fired));
        Assert.Equal(BuffChangeKind.Removed, bc.Kind);
        Assert.Empty(svc.BuffsFor(entityId));
    }

    [Fact]
    public void ApplyBuffEvents_ChangedFields_EmitsRefreshed()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var entityId = new EntityId(1234L);
        var v1 = new ActiveBuff(100, 9001, 1, EntityId.None, 1, 1, 1000, 5000);
        svc.ApplyBuffEvents(entityId, new[] { v1 }, System.Array.Empty<int>(), 1000);

        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;
        svc.Drain();
        fired.Clear();

        var v2 = v1 with { Stacks = 3, Layer = 2 };
        svc.ApplyBuffEvents(entityId, new[] { v2 }, System.Array.Empty<int>(), 2000);
        svc.Drain();

        var bc = Assert.IsType<CombatEvent.BuffChanged>(Assert.Single(fired));
        Assert.Equal(BuffChangeKind.Refreshed, bc.Kind);
        Assert.Equal(3, bc.Stacks);
        Assert.Equal(2, bc.Layer);
    }

    [Fact]
    public void ApplyBuffEvents_PartialChange_MergesNonZero_PreservesBaseId()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var local = new EntityId(0x0000_0001_0000_0280L);
        svc.SetLocalEntityId(local);
        svc.ApplyBuffEvents(local,
            new[] { new ActiveBuff(100, 2110056, 1, EntityId.None, 1, 1, 1000, 6000) },
            System.Array.Empty<int>(), 1000);

        // Partial BuffChange-style upsert: BaseId=0, only Stacks set.
        svc.ApplyBuffEvents(local,
            new[] { new ActiveBuff(100, 0, 0, EntityId.None, 3, 0, 0, 0) },
            System.Array.Empty<int>(), 2000);

        var b = Assert.Single(svc.LocalBuffs);
        Assert.Equal(3,       b.Stacks);        // updated
        Assert.Equal(2110056, b.BaseId);        // preserved
        Assert.Equal(6000,    b.DurationMs);    // preserved
        Assert.Equal(1000,    b.CreateTimeMs);  // preserved
    }

    [Fact]
    public void ApplyBuffEvents_IdenticalUpsertTwice_EmitsOnlyOneApplied()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        var entityId = new EntityId(0x0000_0001_0000_0040L);
        var buff = new ActiveBuff(23, 9023, 1, EntityId.None, 1, 1, 1000, 5000);
        svc.ApplyBuffEvents(entityId, new[] { buff }, System.Array.Empty<int>(), 1000);
        svc.ApplyBuffEvents(entityId, new[] { buff }, System.Array.Empty<int>(), 1000);
        svc.Drain();

        var bc = Assert.IsType<CombatEvent.BuffChanged>(Assert.Single(fired));
        Assert.Equal(BuffChangeKind.Applied, bc.Kind);
    }

    // --- Phase 3b Batch 2: IngestDamage (replaces ProcessHpDelta) ---

    private static SyncDamageInfoMsg MakeDamage(
        int  hpLessen     = 0,
        int  value        = 0,
        int  luckyValue   = 0,
        int  actualValue  = 0,
        int  shieldLessen = 0,
        long attacker     = 0,
        long topSummoner  = 0,
        int  ownerId      = 0,
        int  typeFlag     = 0,
        int  type         = 0,
        bool isDead       = false,
        int  property     = 0,
        int  damageSource = 0)
        => new SyncDamageInfoMsg(
            DamageSource:      damageSource,
            Type:              type,
            TypeFlag:          typeFlag,
            Value:             value,
            ActualValue:       actualValue,
            LuckyValue:        luckyValue,
            HpLessenValue:     hpLessen,
            ShieldLessenValue: shieldLessen,
            AttackerUuid:      attacker,
            TopSummonerId:     topSummoner,
            OwnerId:           ownerId,
            IsMiss:            false,
            IsCrit:            false,
            IsDead:            isDead,
            Property:          property);

    [Fact]
    public void IngestDamage_PopulatedMessage_EmitsDamageDealtWithMappedFields()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        var target = new EntityId(0x0000_0001_0000_0040L);
        var msg = MakeDamage(
            hpLessen:     900,
            value:        1234,
            luckyValue:   50,
            actualValue:  1100,
            shieldLessen: 100,
            attacker:     0xDEADBEEFL,
            topSummoner:  0xCAFEBABEL,
            ownerId:      7777,
            typeFlag:     0b101,       // crit + lucky
            type:         0,           // Normal
            isDead:       true,
            property:     1,           // Fire
            damageSource: 1);          // Bullet

        svc.IngestDamage(msg, target, timestampMs: 200);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.Equal(target,                          dd.TargetId);
        Assert.Equal(new EntityId(0xCAFEBABEL),       dd.SourceId);   // TopSummonerId preferred
        Assert.Equal(7777,                            dd.SkillId);
        Assert.Equal(1234,                            dd.Amount);     // Value preferred over HpLessen
        Assert.Equal(1100,                            dd.ActualAmount);
        Assert.Equal(100,                             dd.ShieldAbsorbed);
        Assert.True (dd.IsCrit);
        Assert.True (dd.IsLucky);
        Assert.False(dd.IsHeal);
        Assert.True (dd.IsDead);
        Assert.Equal(DamageElement.Fire,              dd.Element);
        Assert.Equal(DamageSourceKind.Bullet,         dd.SourceKind);
        Assert.Equal(200L,                            dd.TimestampMs);
    }

    [Fact]
    public void IngestDamage_ZeroDamage_Suppressed()
    {
        // Pure miss / fully-absorbed / immune hit: all three damage candidates
        // are zero. IngestDamage must not emit — otherwise DPS aggregators
        // would count zero-rows against hit counts.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(), new EntityId(1), 100);
        svc.Drain();

        Assert.Empty(fired);
    }

    [Fact]
    public void IngestDamage_ValueTakesPrecedenceOverHpLessen()
    {
        // Visual-match rule (2026-05-24): floating damage number on-screen is
        // the gross "Value"; aggregate that so the meter matches what the
        // player sees. HpLessenValue is post-mitigation HP delta — smaller,
        // and not what the user expects to see in the total.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 700, value: 1000, luckyValue: 5), new EntityId(1), 100);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.Equal(1000, dd.Amount);
    }

    [Fact]
    public void IngestDamage_HpLessenUsedWhenValueZero()
    {
        // Mid-tier of the precedence: HpLessenValue used when Value is zero.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 250, value: 0, luckyValue: 50), new EntityId(1), 100);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.Equal(250, dd.Amount);
    }

    [Fact]
    public void IngestDamage_LuckyValueFallback_WhenBothZero()
    {
        // Bottom of the precedence: lucky-only hit (no HP reduction registered
        // as such, no Value field set, only LuckyValue carries the amount).
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 0, value: 0, luckyValue: 42), new EntityId(1), 100);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.Equal(42, dd.Amount);
    }

    [Fact]
    public void IngestDamage_TopSummonerIdPreferredOverAttackerUuid()
    {
        // Pet / totem damage: TopSummonerId is the player who owns the summon;
        // AttackerUuid is the summon itself. Attribution must roll up to the
        // owner so DPS aggregates count the player, not the pet.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 100, attacker: 0x10L, topSummoner: 0x20L),
                         new EntityId(1), 100);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.Equal(new EntityId(0x20L), dd.SourceId);
    }

    [Fact]
    public void IngestDamage_NoTopSummoner_FallsBackToAttackerUuid()
    {
        // Direct caster (no pet / totem): TopSummonerId is zero, AttackerUuid
        // is the player. SourceId should be the player.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 100, attacker: 0xABCL, topSummoner: 0),
                         new EntityId(1), 100);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.Equal(new EntityId(0xABCL), dd.SourceId);
    }

    [Theory]
    [InlineData(0b000, false, false)]
    [InlineData(0b001, true,  false)]   // crit only
    [InlineData(0b100, false, true)]    // lucky only
    [InlineData(0b101, true,  true)]    // crit + lucky
    [InlineData(0b010, false, false)]   // bit 1 set is neither — ignored
    public void IngestDamage_TypeFlagBitsDecoded(int typeFlag, bool expectCrit, bool expectLucky)
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 100, typeFlag: typeFlag),
                         new EntityId(1), 100);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.Equal(expectCrit,  dd.IsCrit);
        Assert.Equal(expectLucky, dd.IsLucky);
    }

    [Fact]
    public void IngestDamage_HealType_IsHealTrue()
    {
        // EDamageType.Heal = 2 (verified against the BPSR-Meter reference at
        // python-src/proto/enums/e_damage_type.py). Plugins like CombatMeter
        // filter out heal rows; CombatService must surface the discriminator
        // accurately so the filter is reliable.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 500, type: 2 /* Heal */),
                         new EntityId(1), 100);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.True(dd.IsHeal);
    }

    [Fact]
    public void IngestDamage_NormalType_IsHealFalse()
    {
        // Type=0 (Normal) is not a heal. Belt-and-braces against the inverse
        // bug — if someone flips the comparison the heal flag would default
        // true and CombatMeter would filter ALL damage.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 500, type: 0), new EntityId(1), 100);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.False(dd.IsHeal);
    }

    [Fact]
    public void IngestDamage_AllAmountsZero_NoEventEmitted()
    {
        // Belt-and-braces explicit form of the zero-suppression rule. Some
        // misses arrive with isDead/IsCrit flags set but no damage; the
        // suppression must catch them all regardless of other flags.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 0, value: 0, luckyValue: 0,
                                    typeFlag: 0b001, isDead: true),
                         new EntityId(1), 100);
        svc.Drain();

        Assert.Empty(fired);
    }

    [Fact]
    public void IngestDamage_HealType_StillEmitsEvent()
    {
        // Even though CombatMeter filters out heal rows in its UI, the combat
        // event itself must fire so other plugins (e.g. a future Healing
        // tracker) can observe heals. Heals only get suppressed when amount
        // is zero, not because IsHeal is true.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 250, type: 2 /* Heal */), new EntityId(1), 100);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.True(dd.IsHeal);
        Assert.Equal(250, dd.Amount);
    }

    [Fact]
    public void IngestDamage_NoAttackerNoTopSummoner_SourceIdIsNone()
    {
        // Environmental / unattributed damage: both attacker and top-summoner
        // are zero. SourceId should be EntityId.None so the event surface is
        // unambiguous about "we don't know who did this".
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        svc.IngestDamage(MakeDamage(hpLessen: 100, attacker: 0, topSummoner: 0),
                         new EntityId(0x0000_0001_0000_0040L), 100);
        svc.Drain();

        var dd = Assert.IsType<CombatEvent.DamageDealt>(Assert.Single(fired));
        Assert.Equal(EntityId.None, dd.SourceId);
    }

    [Fact]
    public void IngestDamage_MultipleSequentialIngests_EachEmitsSeparateEvent()
    {
        // Each IngestDamage call corresponds to one wire row; downstream
        // listeners count by event, so per-call accounting must stay 1:1.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var fired = new List<CombatEvent>();
        svc.CombatEventOccurred += fired.Add;

        var target = new EntityId(0x0000_0001_0000_0040L);
        svc.IngestDamage(MakeDamage(hpLessen: 100, attacker: 0x10L), target, 100);
        svc.IngestDamage(MakeDamage(hpLessen: 200, attacker: 0x20L), target, 200);
        svc.IngestDamage(MakeDamage(hpLessen: 300, attacker: 0x30L), target, 300);
        svc.Drain();

        Assert.Equal(3, fired.Count);
        Assert.Equal(100, ((CombatEvent.DamageDealt)fired[0]).Amount);
        Assert.Equal(200, ((CombatEvent.DamageDealt)fired[1]).Amount);
        Assert.Equal(300, ((CombatEvent.DamageDealt)fired[2]).Amount);
        Assert.Equal(new EntityId(0x10L), ((CombatEvent.DamageDealt)fired[0]).SourceId);
        Assert.Equal(new EntityId(0x20L), ((CombatEvent.DamageDealt)fired[1]).SourceId);
        Assert.Equal(new EntityId(0x30L), ((CombatEvent.DamageDealt)fired[2]).SourceId);
    }

    // --- Task 24: OnEntityDisappeared clears buff cache ---

    [Fact]
    public void OnEntityDisappeared_RemovesBuffsCache()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var entityId = new EntityId(0x0000_0001_0000_0040L);

        // Seed buffs cache.
        var buff = new ActiveBuff(1, 1, 1, EntityId.None, 1, 1, 0, 1000);
        svc.ApplyBuffEvents(entityId, new[] { buff }, System.Array.Empty<int>(), 0);
        svc.Drain();
        Assert.Single(svc.BuffsFor(entityId));

        svc.OnEntityDisappeared(entityId);

        Assert.Empty(svc.BuffsFor(entityId));
    }

    // --- Entity-name resolution (AttrName / EAttrType=1) ---

    [Fact]
    public void UpdateEntityName_ThenGetEntityName_ReturnsName()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var entityId = new EntityId(0x0000_0001_0000_5188L);

        Assert.Null(svc.GetEntityName(entityId));

        svc.UpdateEntityName(entityId, "Doraemon");

        Assert.Equal("Doraemon", svc.GetEntityName(entityId));
    }

    [Fact]
    public void OnEntityDisappeared_RemovesEntityName()
    {
        // Names must evict alongside the buffs cache when the server reports
        // an entity left AoI — otherwise the dictionary grows unbounded across
        // a long play session as players come/go through nearby zones.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        var entityId = new EntityId(0x0000_0001_0000_5188L);

        svc.UpdateEntityName(entityId, "Doraemon");
        Assert.Equal("Doraemon", svc.GetEntityName(entityId));

        svc.OnEntityDisappeared(entityId);

        Assert.Null(svc.GetEntityName(entityId));
    }

    // --- Task B1 (C-13): LocalCooldowns snapshot cache ---

    [Fact]
    public void LocalCooldowns_TwoReadsWithNoMutation_ReturnSameCachedReference()
    {
        // With no mutation between reads the snapshot must be the cached
        // instance — zero per-call allocation on the hot path (CooldownBar
        // reads this every frame).
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        svc.SetLocalCooldowns(new[] { new SkillCooldown(1, 0, 5000, SkillCooldownKind.Normal, 0, 5000) });

        var first  = svc.LocalCooldowns;
        var second = svc.LocalCooldowns;

        Assert.Same(first, second);
    }

    [Fact]
    public void LocalCooldowns_AfterMutation_ReturnsFreshSnapshot()
    {
        // After SetLocalCooldowns bumps the version the next read must
        // return a new snapshot reflecting the mutation.
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache());
        svc.SetLocalCooldowns(new[] { new SkillCooldown(1, 0, 5000, SkillCooldownKind.Normal, 0, 5000) });

        var first = svc.LocalCooldowns;
        Assert.Single(first);

        svc.SetLocalCooldowns(new[] { new SkillCooldown(2, 0, 3000, SkillCooldownKind.Normal, 0, 3000) });

        var second = svc.LocalCooldowns;
        Assert.Equal(2, second.Count); // merged: skill 1 + skill 2

        // Must be a different object — the version guard must have rebuilt it.
        Assert.NotSame(first, second);
    }
}
