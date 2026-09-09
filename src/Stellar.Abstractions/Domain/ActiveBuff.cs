namespace Stellar.Abstractions.Domain;

/// <summary>
/// One buff instance currently active on an entity, as observed on the combat wire.
/// </summary>
/// <param name="BuffUuid">Per-instance unique id for this buff application.</param>
/// <param name="BaseId">Game-table base buff id.</param>
/// <param name="Level">Buff level as reported by the wire.</param>
/// <param name="FirerId">Entity that applied the buff (wire <c>FireUuid</c>); <see cref="EntityId.None"/> when unknown.</param>
/// <param name="Stacks">Current stack count.</param>
/// <param name="Layer">Buff layer index.</param>
/// <param name="CreateTimeMs">Server epoch ms when the buff was created.</param>
/// <param name="DurationMs">Total duration in milliseconds.</param>
/// <param name="SourceKind">Origin domain of the buff (wire <c>FightSourceInfo.fight_source_type</c>, EFightSource: 0 Skill, 1 Buff, 6 Talent, 9 Mod, 10 Equip, 13 SeasonTalent, 1000+ scene/affix, 10000 Other); 0 when absent.</param>
/// <param name="SourceId">Config id inside <paramref name="SourceKind"/>'s domain — the skill id when <paramref name="SourceKind"/> is 0; 0 when absent.</param>
public readonly record struct ActiveBuff(
    int      BuffUuid,
    int      BaseId,
    int      Level,
    EntityId FirerId,
    int      Stacks,
    int      Layer,
    long     CreateTimeMs,
    int      DurationMs,
    int      SourceKind = 0,
    int      SourceId = 0);
