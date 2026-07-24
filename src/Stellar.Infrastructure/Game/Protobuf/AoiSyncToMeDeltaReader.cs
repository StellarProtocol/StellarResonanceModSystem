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
    public static bool TryReadOuter(ReadOnlyMemory<byte> payload, out AoiSyncToMeDeltaMsg msg)
    {
        var span = payload.Span;
        int pos = 0;
        while (pos < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref pos, out var field, out var wire))
            {
                msg = default; return false;
            }
            if (field == 1 && wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(span, ref pos, out var innerSpan))
                {
                    msg = default; return false;
                }
                var inner = payload.Slice(pos - innerSpan.Length, innerSpan.Length);
                return TryRead(inner, out msg);
            }
            if (!WireProtocol.SkipField(span, ref pos, wire))
            {
                msg = default; return false;
            }
        }
        msg = default; return false;
    }

    public static bool TryRead(ReadOnlyMemory<byte> payload, out AoiSyncToMeDeltaMsg msg)
    {
        var span = payload.Span;
        long uuid = 0;
        AoiSyncDeltaMsg? baseDelta = null;
        // Lazy — most self-deltas carry zero cooldown rows (audit finding 9); the retained
        // snapshot contract (SetLocalCooldowns keeps the list) means it must stay a FRESH
        // allocation when rows exist, never a reused scratch instance.
        List<SkillCooldown>? cds = null;
        int pos = 0;
        while (pos < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref pos, out var field, out var wire)) { msg = default; return false; }
            switch ((field, wire))
            {
                case (1, 2):
                    if (!WireProtocol.TryReadLengthDelimited(span, ref pos, out var bd)) { msg = default; return false; }
                    if (!AoiSyncDeltaReader.TryReadDelta(payload.Slice(pos - bd.Length, bd.Length), out var d)) { msg = default; return false; }
                    baseDelta = d;
                    break;
                case (3, 2):
                    if (!WireProtocol.TryReadLengthDelimited(span, ref pos, out var cb)) { msg = default; return false; }
                    if (!SkillCDInfoReader.TryRead(cb, out var cd)) { msg = default; return false; }
                    (cds ??= new List<SkillCooldown>(8)).Add(cd);
                    break;
                case (5, 0):
                    if (!WireProtocol.TryReadVarint(span, ref pos, out var u)) { msg = default; return false; }
                    uuid = (long)u;
                    break;
                default:
                    if (!WireProtocol.SkipField(span, ref pos, wire)) { msg = default; return false; }
                    break;
            }
        }
        msg = new AoiSyncToMeDeltaMsg(uuid, baseDelta, (IReadOnlyList<SkillCooldown>?)cds ?? Array.Empty<SkillCooldown>());
        return true;
    }
}
