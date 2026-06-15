using System;
using Stellar.Abstractions.Domain;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Pure protobuf parser for <c>stru_skill_c_d_info.proto</c> rows that ride
/// inside <c>AoiSyncToMeDelta.SyncSkillCDs</c>. Side-effect-free; defensive
/// Try* pattern matching the shape established in <see cref="WireProtocol"/>.
///
/// Fields decoded:
/// <list type="bullet">
///   <item>1 = skill_level_id (int32) → <see cref="SkillCooldown.SkillId"/></item>
///   <item>2 = skill_begin_time (int64) → <see cref="SkillCooldown.BeginTimeMs"/></item>
///   <item>3 = duration (int32) → <see cref="SkillCooldown.DurationMs"/></item>
///   <item>4 = skill_cd_type (uint32) → <see cref="SkillCooldownKind"/></item>
///   <item>7 = charge_count (int32) → <see cref="SkillCooldown.ChargeCount"/></item>
///   <item>8 = valid_cd_time (int32) → <see cref="SkillCooldown.ValidCdTimeMs"/></item>
///   <item>9 = sub_cd_ratio (int32) → <see cref="SkillCooldown.SubCdRatio"/></item>
///   <item>10 = sub_cd_fixed (int64) → <see cref="SkillCooldown.SubCdFixedMs"/></item>
///   <item>11 = accelerate_cd_ratio (int32) → <see cref="SkillCooldown.AccelerateCdRatio"/> (haste recovery accel)</item>
/// </list>
/// Field 6 (profession_hold_begin_time) is intentionally skipped — not part
/// of the Phase 3 surface.
/// </summary>
internal static class SkillCDInfoReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out SkillCooldown cd)
    {
        int  skillId = 0;
        long beginMs = 0;
        int  durMs   = 0;
        uint cdType  = 0;
        int  charges = 0;
        int  validCd = 0;
        int  subRatio = 0;
        long subFixed = 0;
        int  accel = 0;

        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { cd = default; return false; }
            switch ((field, wire))
            {
                case (1, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v1)) { cd = default; return false; } skillId = (int)v1; break;
                case (2, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v2)) { cd = default; return false; } beginMs = (long)v2; break;
                case (3, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v3)) { cd = default; return false; } durMs   = (int)v3; break;
                case (4, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v4)) { cd = default; return false; } cdType  = (uint)v4; break;
                case (7, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v7)) { cd = default; return false; } charges = (int)v7; break;
                case (8, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v8)) { cd = default; return false; } validCd = (int)v8; break;
                case (9, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v9)) { cd = default; return false; } subRatio = (int)v9; break;
                case (10, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v10)) { cd = default; return false; } subFixed = (long)v10; break;
                case (11, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v11)) { cd = default; return false; } accel = (int)v11; break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) { cd = default; return false; }
                    break;
            }
        }

        cd = new SkillCooldown(
            SkillId: skillId,
            BeginTimeMs: beginMs,
            DurationMs: durMs,
            Kind: (SkillCooldownKind)cdType,
            ChargeCount: charges,
            ValidCdTimeMs: validCd,
            SubCdRatio: subRatio,
            SubCdFixedMs: subFixed,
            AccelerateCdRatio: accel);
        return true;
    }
}
