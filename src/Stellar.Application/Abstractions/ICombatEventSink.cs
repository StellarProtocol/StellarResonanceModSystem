using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

// ---------------------------------------------------------------------------
// Sub-interfaces (facade-inheritance; each has a single cohesive concern)
// ---------------------------------------------------------------------------

/// <summary>
/// Raw event ingestion facet: accepts incoming combat events, damage records,
/// and the server clock baseline from the Infrastructure probe.
/// </summary>
internal interface ICombatEventIngestion
{
    void EnqueueEvent(CombatEvent evt);

    /// <summary>
    /// Ingest a single pre-attributed <c>SyncDamageInfo</c> record sourced
    /// from <c>AoiSyncDelta.SkillEffects.Damages[]</c>. Computes the effective
    /// damage amount, source attribution, and bit-flags, then emits a
    /// <see cref="CombatEvent.DamageDealt"/> onto the queue. Returns silently
    /// when the resulting damage is zero (e.g. pure miss / fully absorbed).
    /// </summary>
    void IngestDamage(SyncDamageInfoMsg msg, EntityId targetId, long timestampMs);

    void SetServerNowMs(long epochMs);
}

/// <summary>
/// Buff and cooldown state facet: receives local-player buff/cooldown snapshots
/// and maintains a per-entity buff cache with event-accumulation semantics.
/// </summary>
internal interface ICombatBuffSink
{
    void SetLocalCooldowns(IReadOnlyList<SkillCooldown> cooldowns);

    /// <summary>
    /// Apply a batch of buff events for an entity (AoiSyncDelta field 10,
    /// BuffEffectSync). <paramref name="upserts"/> are added/refreshed buffs
    /// (keyed by <c>ActiveBuff.BuffUuid</c>); <paramref name="removedBuffUuids"/>
    /// are removed. The local entity's accumulated set is exposed via
    /// <c>ICombatSnapshot.LocalBuffs</c>; each change emits
    /// <c>CombatEvent.BuffChanged</c>.
    /// </summary>
    void ApplyBuffEvents(EntityId entityId, IReadOnlyList<ActiveBuff> upserts,
                         IReadOnlyList<int> removedBuffUuids, long timestampMs);

    /// <summary>
    /// Drop all accumulated buffs (every entity + the local snapshot). Called on
    /// scene change: buffs are scene-scoped and the server clears them without
    /// sending per-buff remove events, so the event-accumulation set would
    /// otherwise show stale debuffs (e.g. a lockout) after a zone transition.
    /// </summary>
    void ClearAllBuffs();
}

/// <summary>
/// Entity cache facet: tracks entity presence, names, vitals, and team
/// membership as observations arrive from the network thread.
/// </summary>
internal interface ICombatEntityCache
{
    /// <summary>Idempotent — only the first non-None value sticks.</summary>
    void SetLocalEntityId(EntityId entityId);

    /// <summary>Called when SyncNearEntities reports a disappear — sink drops cache rows.</summary>
    void OnEntityDisappeared(EntityId entityId);

    /// <summary>
    /// Drop ALL per-entity cache rows (vitals/dps/hps/team/fight-point/skills/attrs/equip/fashion/names).
    /// Called on scene change alongside <see cref="ICombatBuffSink.ClearAllBuffs"/>: combat mobs are often
    /// touched only via damage packets and never get a matching SyncNearEntities disappear, so without a
    /// scene-boundary reset their accumulators pile up across dungeon re-entries (GC pressure → FPS decay).
    /// </summary>
    void ResetEntities();

    /// <summary>
    /// Update the resolved display name for an entity. Idempotent — only the
    /// first non-empty value sticks per (entity, value) pair; identical
    /// subsequent calls are no-ops, and empty/null names are ignored so a
    /// transient empty AttrName row can't blow away a previously resolved
    /// name.
    /// </summary>
    void UpdateEntityName(EntityId entityId, string name);

    /// <summary>
    /// Update HP / MaxHP for an entity from an <c>AttrHp</c> / <c>AttrMaxHp</c>
    /// observation. Either field may be -1 to signal "no update for this side"
    /// (e.g. when only AttrHp was present in the current AttrCollection row).
    /// </summary>
    void UpdateEntityVitals(EntityId entityId, long hp, long maxHp);

    /// <summary>
    /// Update the team id for an entity from an <c>AttrTeamId</c> (id=194)
    /// observation. <paramref name="teamId"/> of 0 means the entity is solo
    /// or has left their team. Same idempotent semantics as
    /// <see cref="UpdateEntityName"/>.
    /// </summary>
    void UpdateEntityTeamId(EntityId entityId, long teamId);

    /// <summary>
    /// Update the ability/combat score for an entity from an <c>AttrFightPoint</c>
    /// (id=10030) observation. Idempotent; same semantics as <see cref="UpdateEntityTeamId"/>.
    /// </summary>
    void UpdateEntityFightPoint(EntityId entityId, long fightPoint);

    /// <summary>
    /// Update the equipped skill loadout for an entity from an
    /// <c>AttrSkillLevelIdList</c> (id=116) observation. An empty list is
    /// ignored so a transient empty attr row can't blow away a previously
    /// resolved loadout.
    /// </summary>
    void UpdateEntitySkillLevels(EntityId entityId, IReadOnlyList<SkillLevel> skills);
}

/// <summary>
/// Entity inspector-detail facet: stores the raw per-entity attribute map and
/// equipment loadout that the entity inspector reads (level, profession,
/// season level, gear). Separate from <see cref="ICombatEntityCache"/> so the
/// hot-path presence/vitals cache stays cohesive.
/// </summary>
internal interface ICombatEntityDetailSink
{
    /// <summary>
    /// Store a single decoded scalar attribute value for an entity (keyed by
    /// <c>zproto.EAttrType</c> id). Populates the per-entity attr map the
    /// inspector reads (level, profession, season level, fight point, etc.).
    /// Only call with scalar/varint-decoded attrs — never with string- or
    /// packed-typed attrs.
    /// </summary>
    void SetEntityAttribute(EntityId entityId, int attrId, long value);

    /// <summary>
    /// Replace the cached equipment loadout for an entity from an
    /// <c>AttrEquipData</c> (id=200) observation, already decoded via
    /// <c>AttrEquipDataReader.Read</c>.
    /// </summary>
    void SetEntityEquipment(EntityId entityId, IReadOnlyList<EquipNineEntry> equip);

    /// <summary>
    /// Replace the cached worn-cosmetics list for an entity from an
    /// <c>AttrFashionData</c> (id=201) observation, already decoded via
    /// <c>AttrFashionDataReader.Read</c>.
    /// </summary>
    void SetEntityFashion(EntityId entityId, IReadOnlyList<FashionEntry> fashion);
}

// ---------------------------------------------------------------------------
// Facade — zero declared members; all members come from the sub-interfaces.
// Existing consumers (Infrastructure probe) and implementors (CombatService)
// are unaffected.
// ---------------------------------------------------------------------------

/// <summary>
/// Outbound: methods the Infrastructure-side combat probe calls to forward
/// observations into Application. Implemented by <c>CombatService</c>. All
/// calls happen on the network receive thread; the sink is responsible for
/// any queueing required to deliver events on the main thread later.
/// </summary>
internal interface ICombatEventSink : ICombatEventIngestion, ICombatBuffSink, ICombatEntityCache, ICombatEntityDetailSink { }
