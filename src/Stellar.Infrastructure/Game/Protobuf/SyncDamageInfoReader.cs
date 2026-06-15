using System;
using Stellar.Wire;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Pure parser for <c>SyncDamageInfo</c>. Hand-rolled, side-effect-free,
/// defensive Try* pattern matching the other readers in this folder.
///
/// The on-wire shape has 25 fields; for v1 DPS attribution we read 15 and
/// skip the rest. Field-number table (verified against BPSR-Meter's
/// <c>bp_main.py</c> SyncDamageInfo class):
///
/// <list type="table">
///   <item><description>1  DamageSource (int32 — EDamageSource)</description></item>
///   <item><description>2  IsMiss (bool)</description></item>
///   <item><description>3  IsCrit (bool) — redundant with TypeFlag bit 0</description></item>
///   <item><description>4  Type (int32 — EDamageType: Damage / Heal)</description></item>
///   <item><description>5  TypeFlag (int32 — bit 0 = crit, bit 2 = lucky)</description></item>
///   <item><description>6  Value (int — gross damage)</description></item>
///   <item><description>7  ActualValue (int — post-mitigation)</description></item>
///   <item><description>8  LuckyValue (int — lucky-only fallback)</description></item>
///   <item><description>9  HpLessenValue (int — effective HP reduction)</description></item>
///   <item><description>10 ShieldLessenValue (int)</description></item>
///   <item><description>11 AttackerUuid (int64 — direct caster)</description></item>
///   <item><description>12 OwnerId (int32 — skill_id)</description></item>
///   <item><description>17 IsDead (bool)</description></item>
///   <item><description>18 Property (int32 — EDamageProperty / element)</description></item>
///   <item><description>21 TopSummonerId (int64 — true caster for pets/totems)</description></item>
/// </list>
///
/// Skipped: OwnerLevel(13), OwnerStage(14), HitEventId(15), IsNormal(16),
/// DamagePos(19), PartInfos(20), DamageWeight(22), PassiveUuid(23),
/// IsRainbow(24), DamageMode(25). All consumed via SkipField — adding new
/// fields server-side is forward-compatible.
/// </summary>
internal static class SyncDamageInfoReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out SyncDamageInfoMsg msg)
    {
        int  damageSource = 0, type = 0, typeFlag = 0, value = 0, actualValue = 0;
        int  luckyValue = 0, hpLessenValue = 0, shieldLessenValue = 0, ownerId = 0, property = 0;
        long attackerUuid = 0, topSummonerId = 0;
        bool isMiss = false, isCrit = false, isDead = false;

        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { msg = default; return false; }
            switch ((field, wire))
            {
                case (1, 0):  if (!TryReadInt(payload, ref pos, out damageSource))      { msg = default; return false; } break;
                case (2, 0):  if (!TryReadBool(payload, ref pos, out isMiss))           { msg = default; return false; } break;
                case (3, 0):  if (!TryReadBool(payload, ref pos, out isCrit))           { msg = default; return false; } break;
                case (4, 0):  if (!TryReadInt(payload, ref pos, out type))              { msg = default; return false; } break;
                case (5, 0):  if (!TryReadInt(payload, ref pos, out typeFlag))          { msg = default; return false; } break;
                case (6, 0):  if (!TryReadInt(payload, ref pos, out value))             { msg = default; return false; } break;
                case (7, 0):  if (!TryReadInt(payload, ref pos, out actualValue))       { msg = default; return false; } break;
                case (8, 0):  if (!TryReadInt(payload, ref pos, out luckyValue))        { msg = default; return false; } break;
                case (9, 0):  if (!TryReadInt(payload, ref pos, out hpLessenValue))     { msg = default; return false; } break;
                case (10, 0): if (!TryReadInt(payload, ref pos, out shieldLessenValue)) { msg = default; return false; } break;
                case (11, 0): if (!TryReadLong(payload, ref pos, out attackerUuid))     { msg = default; return false; } break;
                case (12, 0): if (!TryReadInt(payload, ref pos, out ownerId))           { msg = default; return false; } break;
                case (17, 0): if (!TryReadBool(payload, ref pos, out isDead))           { msg = default; return false; } break;
                case (18, 0): if (!TryReadInt(payload, ref pos, out property))          { msg = default; return false; } break;
                case (21, 0): if (!TryReadLong(payload, ref pos, out topSummonerId))    { msg = default; return false; } break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) { msg = default; return false; }
                    break;
            }
        }

        msg = new SyncDamageInfoMsg(
            damageSource, type, typeFlag, value, actualValue,
            luckyValue, hpLessenValue, shieldLessenValue, attackerUuid, topSummonerId,
            ownerId, isMiss, isCrit, isDead, property);
        return true;
    }

    private static bool TryReadInt(ReadOnlySpan<byte> payload, ref int pos, out int value)
    {
        if (!WireProtocol.TryReadVarint(payload, ref pos, out var v)) { value = 0; return false; }
        value = (int)v;
        return true;
    }

    private static bool TryReadLong(ReadOnlySpan<byte> payload, ref int pos, out long value)
    {
        if (!WireProtocol.TryReadVarint(payload, ref pos, out var v)) { value = 0; return false; }
        value = unchecked((long)v);
        return true;
    }

    private static bool TryReadBool(ReadOnlySpan<byte> payload, ref int pos, out bool value)
    {
        if (!WireProtocol.TryReadVarint(payload, ref pos, out var v)) { value = false; return false; }
        value = v != 0;
        return true;
    }
}
