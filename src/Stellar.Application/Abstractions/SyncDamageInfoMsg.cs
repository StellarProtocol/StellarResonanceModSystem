namespace Stellar.Application.Abstractions;

/// <summary>
/// Decoded subset of <c>SyncDamageInfo</c> (BPSR protobuf, field-number table
/// in <c>Stellar.Infrastructure.Game.Protobuf.SyncDamageInfoReader</c>).
///
/// <para>
/// Lives in Application — not Infrastructure — because <see cref="ICombatEventSink.IngestDamage"/>
/// takes it by value and Application can't reference Infrastructure types.
/// The struct is a pure data carrier (no BCL-external deps), so it satisfies
/// Application's "BCL only" constraint.
/// </para>
/// </summary>
internal readonly record struct SyncDamageInfoMsg(
    int  DamageSource,
    int  Type,
    int  TypeFlag,
    int  Value,
    int  ActualValue,
    int  LuckyValue,
    int  HpLessenValue,
    int  ShieldLessenValue,
    long AttackerUuid,
    long TopSummonerId,
    int  OwnerId,
    bool IsMiss,
    bool IsCrit,
    bool IsDead,
    int  Property);
