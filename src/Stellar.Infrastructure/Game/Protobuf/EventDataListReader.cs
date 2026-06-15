using System;
using System.Collections.Generic;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

internal readonly record struct EventDataMsg(
    int                EventType,
    IReadOnlyList<int>  IntParams,
    IReadOnlyList<long> LongParams);

internal readonly record struct EventDataListMsg(long Uuid, IReadOnlyList<EventDataMsg> Events);

/// <summary>
/// Pure parser for the <c>EventDataList</c> / <c>EventData</c> protos that
/// ride inside <c>AoiSyncDelta.EventDataList</c> (field 4):
/// <code>
///   message EventData {
///     int32  event_type    = 1;
///     repeated int32  int_params   = 2;
///     repeated int64  long_params  = 3;
///     repeated float  float_params = 4;
///     repeated string str_params   = 5;
///   }
///   message EventDataList {
///     optional int64 Uuid     = 1;
///     repeated EventData Events = 2;
///   }
/// </code>
/// Repeated scalars are accepted in BOTH packed (wire-type 2, length-prefixed
/// stream of varints) and unpacked (wire-type 0, repeated tag) form. The
/// server appears to emit either depending on field; tolerating both makes
/// the reader robust to schema evolution. <c>float_params</c> and
/// <c>str_params</c> are intentionally skipped — no Phase 3 consumer.
/// </summary>
internal static class EventDataListReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out EventDataListMsg msg)
    {
        long uuid = 0;
        var events = new List<EventDataMsg>(4);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { msg = default; return false; }
            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref pos, out var u)) { msg = default; return false; }
                    uuid = (long)u;
                    break;
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var bytes)) { msg = default; return false; }
                    if (!TryReadEvent(bytes, out var evt)) { msg = default; return false; }
                    events.Add(evt);
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) { msg = default; return false; }
                    break;
            }
        }
        msg = new EventDataListMsg(uuid, events);
        return true;
    }

    private static bool TryReadEvent(ReadOnlySpan<byte> payload, out EventDataMsg evt)
    {
        int eventType = 0;
        var ints  = new List<int>(2);
        var longs = new List<long>(2);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { evt = default; return false; }
            switch ((field, wire))
            {
                case (1, 0):
                    if (!TryReadEventType(payload, ref pos, out eventType)) { evt = default; return false; }
                    break;

                // Unpacked repeated int32
                case (2, 0):
                    if (!ReadUnpackedInt(payload, ref pos, ints)) { evt = default; return false; }
                    break;
                // Packed repeated int32: length-prefixed varint stream
                case (2, 2):
                    if (!ReadPackedInts(payload, ref pos, ints)) { evt = default; return false; }
                    break;

                // Unpacked repeated int64
                case (3, 0):
                    if (!ReadUnpackedLong(payload, ref pos, longs)) { evt = default; return false; }
                    break;
                // Packed repeated int64
                case (3, 2):
                    if (!ReadPackedLongs(payload, ref pos, longs)) { evt = default; return false; }
                    break;

                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) { evt = default; return false; }
                    break;
            }
        }
        evt = new EventDataMsg(eventType, ints, longs);
        return true;
    }

    private static bool TryReadEventType(ReadOnlySpan<byte> payload, ref int pos, out int eventType)
    {
        if (!WireProtocol.TryReadVarint(payload, ref pos, out var v)) { eventType = 0; return false; }
        eventType = (int)v;
        return true;
    }

    private static bool ReadUnpackedInt(ReadOnlySpan<byte> payload, ref int pos, List<int> ints)
    {
        if (!WireProtocol.TryReadVarint(payload, ref pos, out var i)) return false;
        ints.Add((int)i);
        return true;
    }

    private static bool ReadPackedInts(ReadOnlySpan<byte> payload, ref int pos, List<int> ints)
    {
        if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var packed)) return false;
        int ipos = 0;
        while (ipos < packed.Length)
        {
            if (!WireProtocol.TryReadVarint(packed, ref ipos, out var pv)) return false;
            ints.Add((int)pv);
        }
        return true;
    }

    private static bool ReadUnpackedLong(ReadOnlySpan<byte> payload, ref int pos, List<long> longs)
    {
        if (!WireProtocol.TryReadVarint(payload, ref pos, out var l)) return false;
        longs.Add((long)l);
        return true;
    }

    private static bool ReadPackedLongs(ReadOnlySpan<byte> payload, ref int pos, List<long> longs)
    {
        if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var packed)) return false;
        int lpos = 0;
        while (lpos < packed.Length)
        {
            if (!WireProtocol.TryReadVarint(packed, ref lpos, out var pv)) return false;
            longs.Add((long)pv);
        }
        return true;
    }
}
