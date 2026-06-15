using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

internal readonly record struct AoiSyncToMeDeltaMsg(
    long                         Uuid,
    AoiSyncDeltaMsg?             BaseDelta,
    IReadOnlyList<SkillCooldown> Cooldowns);

/// <summary>
/// Pure parser for <c>AoiSyncToMeDelta</c> and its outer
/// <c>SyncToMeDeltaInfo { AoiSyncToMeDelta DeltaInfo = 1 }</c> envelope. This
/// is the per-frame "self" delta — same shape as <see cref="AoiSyncDeltaReader"/>
/// but with skill-cooldown info layered on top.
///
/// Schema (from <c>serv_world_ntf.proto</c>):
/// <code>
///   message AoiSyncToMeDelta {
///     optional AoiSyncDelta BaseDelta      = 1;
///     repeated int64        SyncHateIds    = 2;  // ignored — no Phase 3 consumer
///     repeated SkillCDInfo  SyncSkillCDs   = 3;
///     repeated FightResCD   FightResCDs    = 4;  // ignored
///     optional int64        Uuid           = 5;
///   }
///   message SyncToMeDeltaInfo {
///     AoiSyncToMeDelta DeltaInfo = 1;
///   }
/// </code>
/// </summary>
internal static class AoiSyncToMeDeltaReader
{
    /// <summary>
    /// Parse the outer <c>SyncToMeDeltaInfo</c> envelope and descend into its
    /// single <c>DeltaInfo = 1</c> field. Returns false if the envelope is
    /// missing field 1 entirely — that shape is unexpected for the live wire
    /// path and likely indicates the caller picked the wrong message id.
    /// </summary>
    public static bool TryReadOuter(ReadOnlySpan<byte> payload, out AoiSyncToMeDeltaMsg msg)
    {
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire))
            {
                msg = default; return false;
            }
            if (field == 1 && wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var inner))
                {
                    msg = default; return false;
                }
                return TryRead(inner, out msg);
            }
            if (!WireProtocol.SkipField(payload, ref pos, wire))
            {
                msg = default; return false;
            }
        }
        msg = default; return false;
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out AoiSyncToMeDeltaMsg msg)
    {
        long uuid = 0;
        AoiSyncDeltaMsg? baseDelta = null;
        var cds = new List<SkillCooldown>(8);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { msg = default; return false; }
            switch ((field, wire))
            {
                case (1, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var bd)) { msg = default; return false; }
                    if (!AoiSyncDeltaReader.TryReadDelta(bd, out var d)) { msg = default; return false; }
                    baseDelta = d;
                    break;
                case (3, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var cb)) { msg = default; return false; }
                    if (!SkillCDInfoReader.TryRead(cb, out var cd)) { msg = default; return false; }
                    cds.Add(cd);
                    break;
                case (5, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref pos, out var u)) { msg = default; return false; }
                    uuid = (long)u;
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) { msg = default; return false; }
                    break;
            }
        }
        msg = new AoiSyncToMeDeltaMsg(uuid, baseDelta, cds);
        return true;
    }
}
