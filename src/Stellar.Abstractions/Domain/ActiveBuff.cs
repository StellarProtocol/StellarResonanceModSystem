namespace Stellar.Abstractions.Domain;

/// <summary>
/// One row of <c>BuffInfoSync.BuffInfos</c>. Field names map to the
/// <c>BuffInfo</c> proto.
/// </summary>
public readonly record struct ActiveBuff(
    int      BuffUuid,
    int      BaseId,
    int      Level,
    EntityId FirerId,
    int      Stacks,
    int      Layer,
    long     CreateTimeMs,
    int      DurationMs);
