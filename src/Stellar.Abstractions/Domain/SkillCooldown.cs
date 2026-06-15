namespace Stellar.Abstractions.Domain;

/// <summary>Matches the wire enum <c>ESkillCDType</c>.</summary>
public enum SkillCooldownKind
{
    /// <summary>Standard single-charge cooldown.</summary>
    Normal = 0,
    /// <summary>Multi-charge cooldown (skill can be used up to <c>ChargeCount</c> times before the cooldown starts).</summary>
    Charge = 1,
}

/// <summary>
/// One row of <c>AoiSyncToMeDelta.SyncSkillCDs</c>. Times are server epoch ms.
/// </summary>
/// <param name="SkillId">Leveled SkillFightLevel id (baseSkillId*100 + level).</param>
/// <param name="BeginTimeMs">Cooldown start, server epoch ms.</param>
/// <param name="DurationMs">Base cooldown duration in ms (per charge for charge skills).</param>
/// <param name="Kind">Normal vs Charge.</param>
/// <param name="ChargeCount">Charge count for charge skills.</param>
/// <param name="ValidCdTimeMs">Valid CD time (wire field 8).</param>
/// <param name="SubCdRatio">Cooldown reduction ratio, per-10000 (wire field 9, <c>sub_cd_ratio</c>); 0 = none.</param>
/// <param name="SubCdFixedMs">Flat cooldown reduction in ms (wire field 10, <c>sub_cd_fixed</c>); 0 = none.</param>
/// <param name="AccelerateCdRatio">Cooldown recovery acceleration, per-10000 (wire field 11,
/// <c>accelerate_cd_ratio</c>) — e.g. a haste buff makes recovery run faster than realtime; 0 = realtime.</param>
public readonly record struct SkillCooldown(
    int               SkillId,
    long              BeginTimeMs,
    int               DurationMs,
    SkillCooldownKind Kind,
    int               ChargeCount,
    int               ValidCdTimeMs,
    int               SubCdRatio = 0,
    long              SubCdFixedMs = 0,
    int               AccelerateCdRatio = 0);
