using System;
using System.Collections.Generic;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// One row of <c>AttrCollection.Attrs</c>. <see cref="Id"/> maps to
/// <c>zproto.EAttrType</c> (e.g. AttrHp = 11310, AttrMaxHp = 11320). The raw
/// payload bytes are kept intact so consumers can decode them however the
/// concrete attr type requires (varint for HP, packed floats for Pos, etc.).
/// </summary>
internal readonly record struct AttrMsg(int Id, ReadOnlyMemory<byte> RawData)
{
    /// <summary>
    /// Decode <see cref="RawData"/> as a single varint into an int32.
    /// Returns 0 when the payload is empty or malformed — matches the
    /// defensive Try* convention used throughout the wire parsers.
    /// </summary>
    public int DecodedInt
    {
        get
        {
            int pos = 0;
            return WireProtocol.TryReadVarint(RawData.Span, ref pos, out var v) ? (int)v : 0;
        }
    }

    /// <summary>
    /// Decode <see cref="RawData"/> as a single varint into an int64.
    /// Used for HP/MaxHP (int64 on the wire — entities with HP > 2B clip
    /// when read as int32). Returns 0 on empty/malformed payload.
    /// </summary>
    public long DecodedLong
    {
        get
        {
            int pos = 0;
            return WireProtocol.TryReadVarint(RawData.Span, ref pos, out var v) ? (long)v : 0;
        }
    }

    /// <summary>
    /// Decode <see cref="RawData"/> as a UTF-8 string. Used for string-typed
    /// attrs like <c>AttrName</c> (EAttrType=1) where the wire carries the
    /// raw display name bytes without an inner length-prefix. Returns null
    /// on empty payload or decode failure — callers treat null as "unknown".
    /// </summary>
    public string? DecodedString
    {
        get
        {
            if (RawData.IsEmpty) return null;
            try
            {
                return System.Text.Encoding.UTF8.GetString(RawData.Span);
            }
            catch
            {
                return null;
            }
        }
    }
}

internal readonly record struct AttrCollectionMsg(long Uuid, IReadOnlyList<AttrMsg> Items);

/// <summary>
/// Pure parser for the <c>AttrCollection</c> proto used by
/// <c>AoiSyncDelta.Attrs</c> (field 2). The schema is:
/// <code>
///   message AttrCollection {
///     optional int64 Uuid  = 1;
///     repeated Attr  Attrs = 2;
///   }
///   message Attr {
///     int32 Id      = 1;
///     bytes RawData = 2;
///   }
/// </code>
/// Both messages are parsed defensively — any malformed sub-message causes
/// the top-level read to return false, with no partial state leaking out.
/// </summary>
internal static class AttrCollectionReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out AttrCollectionMsg msg)
    {
        long uuid = 0;
        var items = new List<AttrMsg>(8);
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
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var attrBytes)) { msg = default; return false; }
                    if (!TryReadAttr(attrBytes, out var attr)) { msg = default; return false; }
                    items.Add(attr);
                    break;

                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) { msg = default; return false; }
                    break;
            }
        }
        msg = new AttrCollectionMsg(uuid, items);
        return true;
    }

    private static bool TryReadAttr(ReadOnlySpan<byte> payload, out AttrMsg attr)
    {
        int id = 0;
        ReadOnlyMemory<byte> raw = ReadOnlyMemory<byte>.Empty;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { attr = default; return false; }
            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref pos, out var v)) { attr = default; return false; }
                    id = (int)v;
                    break;
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var bytes)) { attr = default; return false; }
                    // Copy off the span — the input ReadOnlySpan won't outlive this call,
                    // but AttrMsg may be retained by an Application-layer snapshot.
                    raw = bytes.ToArray();
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) { attr = default; return false; }
                    break;
            }
        }
        attr = new AttrMsg(id, raw);
        return true;
    }
}
