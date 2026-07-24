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
    // Memory-based end-to-end so the AttrCollection path (field 2) can slice the source
    // packet array instead of copying per attribute (see AttrCollectionReader.TryRead's
    // contract note). The other sub-readers (events / damages / buffs) decode into value
    // types and keep their span inputs unchanged.
    public static bool TryReadList(ReadOnlyMemory<byte> payload, out IReadOnlyList<AoiSyncDeltaMsg> deltas)
    {
        var span = payload.Span;
        var list = new List<AoiSyncDeltaMsg>(4);
        int pos = 0;
        while (pos < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref pos, out var field, out var wire))
            {
                deltas = Array.Empty<AoiSyncDeltaMsg>();
                return false;
            }
            switch ((field, wire))
            {
                case (1, 2):
                    if (!WireProtocol.TryReadLengthDelimited(span, ref pos, out var bytes))
                    {
                        deltas = Array.Empty<AoiSyncDeltaMsg>();
                        return false;
                    }
                    if (!TryReadDelta(payload.Slice(pos - bytes.Length, bytes.Length), out var d))
                    {
                        deltas = Array.Empty<AoiSyncDeltaMsg>();
                        return false;
                    }
                    list.Add(d);
                    break;
                default:
                    if (!WireProtocol.SkipField(span, ref pos, wire))
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

    public static bool TryReadDelta(ReadOnlyMemory<byte> payload, out AoiSyncDeltaMsg delta)
    {
        var span = payload.Span;
        long uuid = 0;
        AttrCollectionMsg? attrs  = null;
        EventDataListMsg?  events = null;
        BuffEventBatch?    buffEvents = null;
        IReadOnlyList<SyncDamageInfoMsg> damages = Array.Empty<SyncDamageInfoMsg>();
        int pos = 0;
        while (pos < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref pos, out var field, out var wire)) { delta = default; return false; }
            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(span, ref pos, out var u)) { delta = default; return false; }
                    uuid = (long)u;
                    break;
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(span, ref pos, out var ab)) { delta = default; return false; }
                    if (!AttrCollectionReader.TryRead(payload.Slice(pos - ab.Length, ab.Length), out var a)) { delta = default; return false; }
                    attrs = a;
                    break;
                case (4, 2):
                    if (!WireProtocol.TryReadLengthDelimited(span, ref pos, out var eb)) { delta = default; return false; }
                    if (!EventDataListReader.TryRead(eb, out var e)) { delta = default; return false; }
                    events = e;
                    break;
                case (7, 2):
                    if (!WireProtocol.TryReadLengthDelimited(span, ref pos, out var sb)) { delta = default; return false; }
                    if (!SkillEffectReader.TryRead(sb, out var dmgs)) { delta = default; return false; }
                    damages = dmgs;
                    break;
                case (10, 2):
                    if (!WireProtocol.TryReadLengthDelimited(span, ref pos, out var bb)) { delta = default; return false; }
                    buffEvents = BuffEffectSyncReader.TryRead(bb);
                    break;
                default:
                    if (!WireProtocol.SkipField(span, ref pos, wire)) { delta = default; return false; }
                    break;
            }
        }
        delta = new AoiSyncDeltaMsg(uuid, attrs, events, buffEvents, damages);
        return true;
    }
}
