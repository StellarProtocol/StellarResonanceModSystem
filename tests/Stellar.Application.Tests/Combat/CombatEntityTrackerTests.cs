using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// TDD guard for <see cref="CombatEntityTracker"/> (Task D1, C-11).
/// These run GREEN against the CURRENT CombatService code first (baseline
/// verification), and must still pass after the extraction.
/// </summary>
public sealed class CombatEntityTrackerTests
{
    // ── Vitals ──────────────────────────────────────────────────────────────

    [Fact]
    public void GetVitals_UnknownEntity_ReturnsUnknown()
    {
        var tracker = new CombatEntityTracker();
        Assert.Equal(EntityVitals.Unknown, tracker.GetVitals(new EntityId(1)));
    }

    [Fact]
    public void UpdateEntityVitals_ThenGetVitals_ReturnsStoredValues()
    {
        var tracker = new CombatEntityTracker();
        var id = new EntityId(0x0000_0001_0000_0280L);

        tracker.UpdateEntityVitals(id, hp: 8000, maxHp: 10000);

        var v = tracker.GetVitals(id);
        Assert.True(v.IsKnown);
        Assert.Equal(8000L, v.Hp);
        Assert.Equal(10000L, v.MaxHp);
    }

    [Fact]
    public void UpdateEntityVitals_SentinelMinus1_KeepsExistingValue()
    {
        var tracker = new CombatEntityTracker();
        var id = new EntityId(0x0000_0001_0000_0280L);

        tracker.UpdateEntityVitals(id, hp: 5000, maxHp: 10000);
        // -1 sentinel = "no change for this side this tick"
        tracker.UpdateEntityVitals(id, hp: -1, maxHp: -1);

        var v = tracker.GetVitals(id);
        Assert.Equal(5000L, v.Hp);
        Assert.Equal(10000L, v.MaxHp);
    }

    // ── DPS ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GetLiveDps_UnknownSource_ReturnsZero()
    {
        var tracker = new CombatEntityTracker();
        Assert.Equal(0L, tracker.GetLiveDps(new EntityId(1)));
    }

    [Fact]
    public void AccumulateDps_ThenGetLiveDps_ReturnsPositive()
    {
        var tracker = new CombatEntityTracker();
        var src = new EntityId(0x0000_0001_0000_0040L);

        tracker.AccumulateDps(src, timestampMs: 1000, amount: 5000);

        Assert.True(tracker.GetLiveDps(src) > 0);
    }

    [Fact]
    public void AccumulateDps_MultipleHits_AccumulatesTotal()
    {
        var tracker = new CombatEntityTracker();
        var src = new EntityId(0x0000_0001_0000_0040L);

        tracker.AccumulateDps(src, timestampMs: 1000,  amount: 1000);
        tracker.AccumulateDps(src, timestampMs: 11000, amount: 1000);

        // encounter: total=2000 / span=10s → 200 dps
        Assert.Equal(200L, tracker.GetLiveDps(src));
    }

    // ── HPS ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GetLiveHps_UnknownSource_ReturnsZero()
    {
        var tracker = new CombatEntityTracker();
        Assert.Equal(0L, tracker.GetLiveHps(new EntityId(1)));
    }

    [Fact]
    public void AccumulateHps_ThenGetLiveHps_ReturnsPositive()
    {
        var tracker = new CombatEntityTracker();
        var src = new EntityId(0x0000_0001_0000_0040L);

        tracker.AccumulateHps(src, timestampMs: 1000, amount: 3000);

        Assert.True(tracker.GetLiveHps(src) > 0);
    }

    // ── TeamId ───────────────────────────────────────────────────────────────

    [Fact]
    public void GetTeamId_UnknownEntity_ReturnsZero()
    {
        var tracker = new CombatEntityTracker();
        Assert.Equal(0L, tracker.GetTeamId(new EntityId(1)));
    }

    [Fact]
    public void UpdateEntityTeamId_ThenGetTeamId_ReturnsStoredId()
    {
        var tracker = new CombatEntityTracker();
        var id = new EntityId(0x0000_0001_0000_0280L);

        tracker.UpdateEntityTeamId(id, teamId: 99L);

        Assert.Equal(99L, tracker.GetTeamId(id));
    }

    // ── EntityName ───────────────────────────────────────────────────────────

    [Fact]
    public void GetEntityName_UnknownEntity_ReturnsNull()
    {
        var tracker = new CombatEntityTracker();
        Assert.Null(tracker.GetEntityName(new EntityId(1)));
    }

    [Fact]
    public void UpdateEntityName_ThenGetEntityName_ReturnsName()
    {
        var tracker = new CombatEntityTracker();
        var id = new EntityId(0x0000_0001_0000_5188L);

        tracker.UpdateEntityName(id, "Doraemon");

        Assert.Equal("Doraemon", tracker.GetEntityName(id));
    }

    // ── OnEntityDisappeared ──────────────────────────────────────────────────

    [Fact]
    public void OnEntityDisappeared_ClearsAllEntityState()
    {
        var tracker = new CombatEntityTracker();
        // Non-player id (low 16 bits != 640): a mob's name is positional flavor,
        // not identity, so it evicts along with everything else on AOI-disappear.
        // (The player exemption is covered separately below — see
        // Player_name_survives_aoi_disappear.)
        var id = new EntityId(0x0000_0001_0000_0040L);
        Assert.False(id.IsPlayer);

        tracker.UpdateEntityVitals(id, hp: 5000, maxHp: 10000);
        tracker.AccumulateDps(id, timestampMs: 1000, amount: 5000);
        tracker.AccumulateHps(id, timestampMs: 1000, amount: 2000);
        tracker.UpdateEntityTeamId(id, teamId: 5L);
        tracker.UpdateEntityName(id, "Slime");

        tracker.OnEntityDisappeared(id);

        Assert.Equal(EntityVitals.Unknown, tracker.GetVitals(id));
        Assert.Equal(0L, tracker.GetLiveDps(id));
        Assert.Equal(0L, tracker.GetLiveHps(id));
        Assert.Equal(0L, tracker.GetTeamId(id));
        Assert.Null(tracker.GetEntityName(id));
    }

    // ── OnEntityDisappeared: player display-name identity exemption ─────────
    // Mirrors the pre-existing _skillsByEntity exemption: a player walking out
    // of AOI shouldn't lose their resolved display name and degrade meter/
    // history/replay rows to the Player#uid fallback. Mobs are unaffected —
    // their names are positional flavor and their ids recycle.

    [Fact]
    public void Player_name_survives_aoi_disappear()
    {
        var tracker = new CombatEntityTracker();
        var playerId = new EntityId(0x0000_0002_0000_0280L);
        Assert.True(playerId.IsPlayer);

        tracker.UpdateEntityName(playerId, "Doraemon");

        tracker.OnEntityDisappeared(playerId);

        Assert.Equal("Doraemon", tracker.GetEntityName(playerId));
    }

    [Fact]
    public void Mob_name_evicted_on_aoi_disappear()
    {
        var tracker = new CombatEntityTracker();
        var mobId = new EntityId(0x0000_0002_0000_0040L);
        Assert.False(mobId.IsPlayer);

        tracker.UpdateEntityName(mobId, "Slime");

        tracker.OnEntityDisappeared(mobId);

        Assert.Null(tracker.GetEntityName(mobId));
    }

    [Fact]
    public void Player_name_cleared_on_scene_reset()
    {
        var tracker = new CombatEntityTracker();
        var playerId = new EntityId(0x0000_0002_0000_0280L);

        tracker.UpdateEntityName(playerId, "Doraemon");

        tracker.Reset();

        Assert.Null(tracker.GetEntityName(playerId));
    }

    [Fact]
    public void Player_other_state_still_evicts_on_disappear_while_name_survives()
    {
        var tracker = new CombatEntityTracker();
        var playerId = new EntityId(0x0000_0002_0000_0280L);

        tracker.UpdateEntityVitals(playerId, hp: 5000, maxHp: 10000);
        tracker.UpdateEntityName(playerId, "Doraemon");

        tracker.OnEntityDisappeared(playerId);

        Assert.False(tracker.GetVitals(playerId).IsKnown);
        Assert.Equal("Doraemon", tracker.GetEntityName(playerId));
    }

    // ── CombatService integration (delegation path) ──────────────────────────

    [Fact]
    public void CombatService_GetVitals_DelegatesToTracker()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        var id = new EntityId(0x0000_0001_0000_0280L);

        svc.UpdateEntityVitals(id, hp: 3000, maxHp: 8000);

        var v = svc.GetVitals(id);
        Assert.True(v.IsKnown);
        Assert.Equal(3000L, v.Hp);
        Assert.Equal(8000L, v.MaxHp);
    }

    [Fact]
    public void CombatService_GetLiveDps_AccumulatesAfterDrain()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        var src = new EntityId(0x0000_0001_0000_0040L);
        var tgt = new EntityId(0x0000_0001_0000_0080L);

        // DPS accumulation is driven by CombatEvent.DamageDealt flowing through Drain
        var msg = new SyncDamageInfoMsg(
            DamageSource: 0, Type: 0, TypeFlag: 0,
            Value: 0, ActualValue: 0, LuckyValue: 0,
            HpLessenValue: 5000, ShieldLessenValue: 0,
            AttackerUuid: src.Value, TopSummonerId: 0,
            OwnerId: 1, IsMiss: false, IsCrit: false, IsDead: false, Property: 0);
        svc.IngestDamage(msg, tgt, timestampMs: 1000);
        svc.Drain();

        Assert.True(svc.GetLiveDps(src) > 0);
    }

    [Fact]
    public void CombatService_GetTeamId_DelegatesToTracker()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        var id = new EntityId(0x0000_0001_0000_0280L);

        svc.UpdateEntityTeamId(id, teamId: 42L);

        Assert.Equal(42L, svc.GetTeamId(id));
    }

    [Fact]
    public void CombatService_GetEntityName_DelegatesToTracker()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        var id = new EntityId(0x0000_0001_0000_5188L);

        svc.UpdateEntityName(id, "TestPlayer");

        Assert.Equal("TestPlayer", svc.GetEntityName(id));
    }

    [Fact]
    public void CombatService_OnEntityDisappeared_ClearsTrackerState()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        // Non-player id: a mob's name evicts along with vitals/team-id on disappear
        // (the player name exemption is covered by the Player_* tests above).
        var id = new EntityId(0x0000_0001_0000_0040L);
        Assert.False(id.IsPlayer);

        svc.UpdateEntityVitals(id, hp: 5000, maxHp: 10000);
        svc.UpdateEntityTeamId(id, teamId: 5L);
        svc.UpdateEntityName(id, "Hero");
        svc.OnEntityDisappeared(id);

        Assert.Equal(EntityVitals.Unknown, svc.GetVitals(id));
        Assert.Equal(0L, svc.GetTeamId(id));
        Assert.Null(svc.GetEntityName(id));
    }
}
