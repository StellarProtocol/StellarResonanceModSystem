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
    public sealed record BuffChanged(long TimestampMs, EntityId TargetId, int BuffUuid, int BaseId,
        BuffChangeKind Kind, int Stacks, int Layer, int DurationMs) : CombatEvent(TimestampMs);

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
}
