using System;
using System.Collections.Generic;
using Stellar.Wire;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game.Protobuf;

internal readonly record struct AoiSyncDeltaMsg(
    long                              Uuid,
    AttrCollectionMsg?                Attrs,
    EventDataListMsg?                 Events,
    BuffEventBatch?                   BuffEvents,
    IReadOnlyList<SyncDamageInfoMsg>  Damages);

/// <summary>
/// Pure parser for <c>AoiSyncDelta</c> + the surrounding
/// <c>SyncNearDeltaInfo { repeated AoiSyncDelta DeltaInfos = 1 }</c> envelope
/// (both defined in <c>serv_world_ntf.proto</c>).
///
/// AoiSyncDelta has ~14 fields; Phase 3 only consumes:
/// <list type="bullet">
///   <item>1 = Uuid (int64)</item>
///   <item>2 = AttrCollection — HP/MaxHP/etc.</item>
///   <item>4 = EventDataList — skill begin/end / damage / etc.</item>
///   <item>7 = SkillEffect — wrapper carrying SyncDamageInfo damages</item>
///   <item>10 = BuffEffectSync — buff add/remove event stream</item>
/// </list>
/// Everything else (TempAttrs, BulletEvent, ActorBodyPartInfos, passive-skill
/// info, BuffEffect, etc.) is consumed via SkipField. Adding new fields
/// server-side is safe — the parser will silently ignore them.
/// </summary>
internal static class AoiSyncDeltaReader
{
    public static bool TryReadList(ReadOnlySpan<byte> payload, out IReadOnlyList<AoiSyncDeltaMsg> deltas)
    {
        var list = new List<AoiSyncDeltaMsg>(4);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire))
            {
                deltas = Array.Empty<AoiSyncDeltaMsg>();
                return false;
            }
            switch ((field, wire))
            {
                case (1, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var bytes))
                    {
                        deltas = Array.Empty<AoiSyncDeltaMsg>();
                        return false;
                    }
                    if (!TryReadDelta(bytes, out var d))
                    {
                        deltas = Array.Empty<AoiSyncDeltaMsg>();
                        return false;
                    }
                    list.Add(d);
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire))
                    {
                        deltas = Array.Empty<AoiSyncDeltaMsg>();
                        return false;
                    }
                    break;
            }
        }
        deltas = list;
        return true;
    }

    public static bool TryReadDelta(ReadOnlySpan<byte> payload, out AoiSyncDeltaMsg delta)
    {
        long uuid = 0;
        AttrCollectionMsg? attrs  = null;
        EventDataListMsg?  events = null;
        BuffEventBatch?    buffEvents = null;
        IReadOnlyList<SyncDamageInfoMsg> damages = Array.Empty<SyncDamageInfoMsg>();
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { delta = default; return false; }
            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref pos, out var u)) { delta = default; return false; }
                    uuid = (long)u;
                    break;
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var ab)) { delta = default; return false; }
                    if (!AttrCollectionReader.TryRead(ab, out var a)) { delta = default; return false; }
                    attrs = a;
                    break;
                case (4, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var eb)) { delta = default; return false; }
                    if (!EventDataListReader.TryRead(eb, out var e)) { delta = default; return false; }
                    events = e;
                    break;
                case (7, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var sb)) { delta = default; return false; }
                    if (!SkillEffectReader.TryRead(sb, out var dmgs)) { delta = default; return false; }
                    damages = dmgs;
                    break;
                case (10, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var bb)) { delta = default; return false; }
                    buffEvents = BuffEffectSyncReader.TryRead(bb);
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) { delta = default; return false; }
                    break;
            }
        }
        delta = new AoiSyncDeltaMsg(uuid, attrs, events, buffEvents, damages);
        return true;
    }
}
