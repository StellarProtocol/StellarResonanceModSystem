using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Per-entity combat lookups. Covers any entity observed in AOI on the combat
/// wire — not limited to party members or the local player. Mirrors the
/// lookup half of the original mixed combat surface; the polled local snapshot
/// lives on <see cref="ICombatSnapshot"/> and the event stream lives on
/// <see cref="ICombatEvents"/>.
/// </summary>
public interface ICombatLookup
{
    /// <summary>Active buffs on the given entity, derived from observed BuffChanged events. Empty if entity is unknown.</summary>
    IReadOnlyList<ActiveBuff> BuffsFor(EntityId entityId);

    /// <summary>Resolved display name for an entity, or null if unknown.</summary>
    string? GetEntityName(EntityId entityId);

    /// <summary>
    /// Last-known HP / MaxHP snapshot for an entity from the combat wire's
    /// <c>AttrCollection</c> stream. Returns <see cref="EntityVitals.Unknown"/>
    /// for entities not yet observed. Works for every entity in AOI — not just
    /// party members.
    /// </summary>
    EntityVitals GetVitals(EntityId entityId);

    /// <summary>
    /// 5-second sliding-window damage-per-second for a damage source
    /// (<see cref="CombatEvent.DamageDealt.SourceId"/>). Returns 0 for
    /// entities that have never been observed as a damage source, or
    /// when the source's last hit fell out of the 5-second window.
    /// Aggregated automatically as <see cref="CombatEvent.DamageDealt"/>
    /// events flow through — no per-plugin tracking needed.
    /// </summary>
    long GetLiveDps(EntityId sourceId);

    /// <summary>
    /// 5-second sliding-window healing-per-second for a heal source
    /// (<see cref="CombatEvent.DamageDealt.SourceId"/> with <c>IsHeal=true</c>).
    /// Returns 0 for entities that have never been observed as a heal source,
    /// or when the source's last heal fell out of the 5-second window.
    /// Aggregated automatically as <see cref="CombatEvent.DamageDealt"/>
    /// heal events flow through — no per-plugin tracking needed.
    /// </summary>
    long GetLiveHps(EntityId sourceId);

    /// <summary>
    /// Last-known team id for an entity from the <c>AttrTeamId</c> (id=194)
    /// attribute on the combat wire. Returns 0 when the entity is solo,
    /// has never been observed, or is out-of-AOI. Two entities with the same
    /// non-zero team id are teammates. The cleanest way to identify in-AOI
    /// party members — does not require decoding the GrpcTeamNtf service.
    /// </summary>
    long GetTeamId(EntityId entityId);

    /// <summary>
    /// Last-known ability/combat score for an entity from the <c>AttrFightPoint</c>
    /// (id=10030) attribute on the combat wire (the "Ability Score" ZDPS shows).
    /// Returns 0 when never observed / out-of-AOI. Present per-entity for player
    /// (EntChar) entities in <c>SyncNearEntities</c>, so it's available for the
    /// local player and visible party members alike.
    /// </summary>
    long GetFightPoint(EntityId entityId);

    /// <summary>
    /// Last-known equipped skill loadout for an entity, decoded from the
    /// combat wire's <c>AttrSkillLevelIdList</c> attribute (<c>EAttrType</c>=116).
    /// Broadcast per-entity for every player in AOI. Returns an empty list when
    /// the entity has never reported its loadout. Each entry carries skill id,
    /// current level, and tier; callers identify Battle Imagines by checking the
    /// skill's <c>SlotPositionId</c> against the skill table.
    /// </summary>
    IReadOnlyList<SkillLevel> GetSkillLevels(EntityId entityId);
}
