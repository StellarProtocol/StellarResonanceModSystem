using System.Collections.Generic;

namespace Stellar.Abstractions.Domain;

/// <summary>Subset of <c>ESkillEventType</c> exposed on <see cref="CombatEvent.SkillUsed"/>.</summary>
public enum SkillEventPhase
{
    /// <summary>Skill cast begins (initial key-press or begin packet).</summary>
    Begin         = 101,
    /// <summary>One animation stage finished.</summary>
    StageEnd      = 102,
    /// <summary>Accumulate-damage window ended.</summary>
    AccumulateEnd = 103,
    /// <summary>Entire skill finished and all effects resolved.</summary>
    SkillEnd      = 104,
    /// <summary>Animation stage begins.</summary>
    StageBegin    = 105,
}

/// <summary>How a buff's state changed, as reported by <see cref="CombatEvent.BuffChanged"/>.</summary>
public enum BuffChangeKind
{
    /// <summary>Buff was freshly applied to the target.</summary>
    Applied,
    /// <summary>An already-active buff had its duration or stacks refreshed.</summary>
    Refreshed,
    /// <summary>Buff was removed from the target.</summary>
    Removed,
}

/// <summary>
/// Discriminated event raised by <see cref="Services.ICombatEvents"/>. Always fires
/// on the main (Unity) thread.
/// </summary>
public abstract record CombatEvent(long TimestampMs)
{
    /// <summary>A skill was cast or progressed through a phase by the identified caster.</summary>
    /// <param name="TimestampMs">Server epoch timestamp of the event in milliseconds.</param>
    /// <param name="CasterId">Entity that cast the skill.</param>
    /// <param name="SkillId">Game-table skill id.</param>
    /// <param name="Phase">Which phase of the skill lifecycle this event covers.</param>
    public sealed record SkillUsed(long TimestampMs, EntityId CasterId, int SkillId, SkillEventPhase Phase) : CombatEvent(TimestampMs);

    /// <summary>A buff on an entity was applied, refreshed, or removed.</summary>
    /// <param name="TimestampMs">Server epoch timestamp of the event in milliseconds.</param>
    /// <param name="TargetId">Entity whose buff state changed.</param>
    /// <param name="BuffUuid">Per-instance unique id for this buff application.</param>
    /// <param name="BaseId">Game-table base buff id (used for lookup in <c>IGameDataCombat.GetBuff</c>).</param>
    /// <param name="Kind">Whether the buff was applied, refreshed, or removed.</param>
    /// <param name="Stacks">Current stack count after the change.</param>
    /// <param name="Layer">Buff layer index.</param>
    /// <param name="DurationMs">Remaining duration in milliseconds; 0 when removed.</param>
    /// <param name="FirerId">Entity that applied the buff (wire <c>FireUuid</c>); <see cref="EntityId.None"/> when the wire carried none.</param>
    /// <param name="SourceKind">Origin domain (EFightSource: 0 Skill, 1 Buff, 6 Talent, 9 Mod, 10 Equip); 0 when absent.</param>
    /// <param name="SourceId">Config id in <paramref name="SourceKind"/>'s domain (the skill id for kind 0); 0 when absent.</param>
    public sealed record BuffChanged(long TimestampMs, EntityId TargetId, int BuffUuid, int BaseId,
        BuffChangeKind Kind, int Stacks, int Layer, int DurationMs,
        EntityId FirerId = default, int SourceKind = 0, int SourceId = 0) : CombatEvent(TimestampMs);

    /// <summary>Damage or healing was dealt between two entities.</summary>
    /// <param name="TimestampMs">Server epoch timestamp of the event in milliseconds.</param>
    /// <param name="SourceId">Entity that dealt the damage (TopSummonerId ?? AttackerUuid).</param>
    /// <param name="TargetId">Entity that received the damage or healing.</param>
    /// <param name="SkillId">Skill OwnerId associated with this hit.</param>
    /// <param name="Amount">Preferred damage value (Value, else HpLessenValue, else LuckyValue).</param>
    /// <param name="ActualAmount">ActualValue after all reductions.</param>
    /// <param name="ShieldAbsorbed">Portion absorbed by a shield (ShieldLessenValue).</param>
    /// <param name="IsCrit">True when TypeFlag bit 0 is set (critical hit).</param>
    /// <param name="IsLucky">True when TypeFlag bit 2 is set (lucky hit variant).</param>
    /// <param name="IsHeal">True when EDamageType is Heal.</param>
    /// <param name="IsDead">True when the target's HP reached zero from this hit.</param>
    /// <param name="Element">Elemental property of the hit (from SyncDamageInfo.Property).</param>
    /// <param name="SourceKind">Source category of the hit (from SyncDamageInfo.DamageSource).</param>
    public sealed record DamageDealt(long TimestampMs, EntityId SourceId, EntityId TargetId, int SkillId,
        int Amount, int ActualAmount, int ShieldAbsorbed,
        bool IsCrit, bool IsLucky, bool IsHeal, bool IsDead,
        DamageElement Element, DamageSourceKind SourceKind) : CombatEvent(TimestampMs);

    /// <summary>
    /// A summon/pet entity entered AOI (<c>SyncNearEntities.appear</c>) carrying a resolvable
    /// owner attribution. Fired once per appear, only when the entity's <c>AttrCollection</c>
    /// carries <c>AttrTopSummonerId</c> or <c>AttrSummonerId</c> — most appearing entities (players,
    /// unowned mobs) carry neither and never raise this event. Useful as an early, wind-up-free
    /// timestamp anchor for a caster's summon-based action (e.g. a Battle Imagine cast) that is
    /// otherwise only observable once the summon lands its first hit.
    /// </summary>
    /// <param name="TimestampMs">Client receive time in server-epoch milliseconds; the wire appear
    /// message itself carries no timestamp field.</param>
    /// <param name="SummonerId">Owning entity the summon is attributed to (<c>AttrTopSummonerId</c>,
    /// falling back to <c>AttrSummonerId</c> when only that is present).</param>
    /// <param name="SummonId">The summon/pet entity that appeared.</param>
    public sealed record EntitySummonAppeared(long TimestampMs, EntityId SummonerId, EntityId SummonId) : CombatEvent(TimestampMs);

    /// <summary>
    /// One entity's numeric attributes changed in ONE wire packet (AoiSyncDelta attr collection, an
    /// appear, or the local player's enter-scene sheet). Raised for PLAYER entities only, AFTER every
    /// value has reached <see cref="Services.IEntityDetail.GetAttributes"/> — so a subscriber that reads the
    /// sheet on this event already sees the change (late-never-stale). <paramref name="TimestampMs"/> is the
    /// packet's own receive stamp, the same clock its sibling <see cref="BuffChanged"/> events carry, so a
    /// consumer may join the two streams on <c>TimestampMs</c> without a clock conversion (rDPS sheet track,
    /// 2026-09-05 spec § 6.1). Carries only the scalar attrs stored in that packet; HP/name/team/skills ride
    /// their own events and are not repeated here.
    /// </summary>
    /// <param name="TimestampMs">Wire receive time of the packet (client wall clock, Unix ms).</param>
    /// <param name="TargetId">The entity whose attributes changed.</param>
    /// <param name="Attrs">The stored attribute values of this packet; never empty.</param>
    public sealed record EntityAttributesChanged(long TimestampMs, EntityId TargetId, IReadOnlyList<AttrValue> Attrs) : CombatEvent(TimestampMs);

    /// <summary>
    /// An entity's client-side actor/controller state machine entered a new state
    /// Stellar names on <see cref="ActorState"/> (2026-07-28 entity-state-death-signal
    /// spec). This is the client's OWN death/break signal — read from its state
    /// machine, not inferred from HP reaching zero or a damage packet's death flag —
    /// so it fires for scripted kills that never zero the target's HP. Riding this
    /// existing event channel (rather than a new <c>ICombatLookup</c>/<c>ICombatEvents</c>
    /// member) is deliberate: it keeps every combat service interface under the
    /// STELLAR0005 8-member ceiling.
    /// </summary>
    /// <param name="TimestampMs">Server epoch timestamp of the event in milliseconds
    /// (<see cref="Services.ICombatSnapshot.ServerNowMs"/> at the moment the state was
    /// entered — this transition is a local client event, not parsed off a wire packet,
    /// so there is no server-supplied timestamp to prefer).</param>
    /// <param name="TargetId">Entity whose state changed (resolved from the state
    /// controller's <c>Host</c>) — named to match the <c>TargetId</c>/<c>SourceId</c>/
    /// <c>SummonerId</c> convention of its siblings above, not <c>EntityId</c> (the
    /// type it's typed as).</param>
    /// <param name="State">Which state the entity entered.</param>
    public sealed record EntityStateChanged(long TimestampMs, EntityId TargetId, ActorState State) : CombatEvent(TimestampMs);

    /// <summary>
    /// An entity's COMPLETE buff set arrived as one snapshot — an AOI appear (<c>SyncNearEntities</c>
    /// <c>Entity.buff_infos</c>) or the local player's <c>EnterScene</c> entity (spec-from-talent-buffs,
    /// 2026-09-26). Snapshot semantics: <paramref name="Buffs"/> REPLACES whatever the consumer held for
    /// <paramref name="TargetId"/> — buffs it held that are not listed are gone, listed ones are current. Raised
    /// exactly once per snapshot, including when the set is empty (so a consumer can clear), and INSTEAD of
    /// per-buff <see cref="BuffChanged"/> events: a town crowd of 30 players × ~120 buffs is 30 events, not
    /// thousands. Live changes afterwards arrive as per-buff <see cref="BuffChanged"/> events exactly as before.
    /// By the time this event fires, <see cref="Services.ICombatLookup.BuffsFor"/> already returns the new set.
    /// A snapshot the client could not decode completely is skipped (no event, held set untouched).
    /// </summary>
    /// <param name="TimestampMs">Wire receive time of the snapshot packet (client wall clock, Unix ms) — the same
    /// clock its sibling <see cref="BuffChanged"/> events carry.</param>
    /// <param name="TargetId">The entity whose buff set was seeded.</param>
    /// <param name="Buffs">The complete buff set; empty when the entity carries none. Treat as read-only.</param>
    public sealed record EntityBuffsSeeded(long TimestampMs, EntityId TargetId, IReadOnlyList<ActiveBuff> Buffs) : CombatEvent(TimestampMs);

    /// <summary>
    /// The value <see cref="Services.ICombatSpec.GetSubProfession"/> returns for an entity changed
    /// (spec-from-talent-buffs, 2026-09-26). Raised exactly once per REAL change of that value, whether it
    /// came from a talent root buff or from cast inference — never for a no-op re-resolution. Holding the
    /// previous spec across a same-class swap gap (the root buff removed before the new one arrives) is not a
    /// change, so Smite → gap → Lifebind raises ONE event, Smite → Lifebind; a gap that outlives 10 s drops the
    /// talent spec and raises one if the reported value changes. A class change (attr 220) with no
    /// root buff of the new class yet DOES raise one (old spec → the new class's cast-derived spec, or 0).
    /// Changes are evaluated on the main-thread drain, never while a wire packet is mid-ingest, so the
    /// whole-packet net change is reported (a class swap delivering attr 220 and the new talent set together raises one
    /// event, not an intermediate old→0→new pair). Not raised when a scene change resets every spec to 0;
    /// specs re-announce (0 → spec) as entities reappear. Rides the existing <c>ICombatEvents</c> stream so no
    /// combat service interface gains a member.
    /// </summary>
    /// <param name="TimestampMs">Wire receive time (client wall clock, Unix ms) of the update that caused the
    /// change — the buff/attr packet, or the damage event whose cast resolved the spec.</param>
    /// <param name="TargetId">The entity whose spec changed (named to match the <c>TargetId</c> convention of
    /// its siblings).</param>
    /// <param name="OldSubProfessionId">The previously reported sub-profession id (0 = none).</param>
    /// <param name="NewSubProfessionId">The sub-profession id now returned by
    /// <see cref="Services.ICombatSpec.GetSubProfession"/> (0 = none).</param>
    /// <param name="FromTalent"><see langword="true"/> when the new value is talent-derived — the same meaning
    /// as <see cref="Services.ICombatSpec.TryGetTalentSpec"/> returning <see langword="true"/>.</param>
    public sealed record SpecChanged(long TimestampMs, EntityId TargetId, int OldSubProfessionId, int NewSubProfessionId, bool FromTalent) : CombatEvent(TimestampMs);
}
