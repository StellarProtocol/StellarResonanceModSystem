using System;

namespace Stellar.Wire;

/// <summary>
/// Outbound ChitChat REQUEST parsers — read off the raw pre-framing request body the
/// <c>ZRpcImpl.SendBytes</c> prefix sees, so a later Return can be attributed to what was asked.
/// </summary>
public static partial class WireProtocol
{
    /// <summary>
    /// Parse the outer <c>ChitChat.GetChipChatRecords { v_request = 1 : ... }</c> envelope and pull BOTH
    /// <c>GetChipChatRecordsRequest.channel_type</c> (field 2, varint enum) and <c>target_id</c> (field 5,
    /// int64 — the whisper peer's charId; the game sets it only for <c>channel_type = 5</c> private history,
    /// <c>chat_main_vm.lua asyncGetChannelRecord</c>). History REPLIES carry no peer at all, so this request
    /// field is the only way to tag a private batch with its conversation. Returns false on an envelope
    /// mismatch or when channel_type is absent; <paramref name="targetId"/> is 0 when field 5 is absent.
    /// Unlike <see cref="ParseChannelTypeFromGetChipChatRecordsRequest(ReadOnlySpan{byte})"/> (which stops at
    /// field 2) this scans the whole inner message — proto3 serializes in field order, so field 5 follows 2.
    /// </summary>
    public static bool TryParseGetChipChatRecordsRequest(ReadOnlySpan<byte> span, out int channelType, out long targetId)
    {
        channelType = -1;
        targetId = 0;
        if (!TryUnwrapVRequest(span, out var inner)) return false;

        int pos = 0;
        while (pos < inner.Length)
        {
            if (!TryReadTag(inner, ref pos, out var f, out var w)) break;
            if (f == 2 && w == 0)
            {
                if (!TryReadVarint(inner, ref pos, out var v)) break;
                channelType = (int)v;
            }
            else if (f == 5 && w == 0)
            {
                if (!TryReadVarint(inner, ref pos, out var t)) break;
                targetId = unchecked((long)t);
            }
            else if (!SkipField(inner, ref pos, w)) break;
        }
        return channelType >= 0;
    }

    /// <summary>
    /// Unwrap a ChitChat method envelope <c>{ v_request = 1 : &lt;Request&gt; }</c> (field 1,
    /// length-delimited) into the inner request bytes. False on a shape mismatch.
    /// </summary>
    private static bool TryUnwrapVRequest(ReadOnlySpan<byte> span, out ReadOnlySpan<byte> inner)
    {
        inner = default;
        int pos = 0;
        if (!TryReadTag(span, ref pos, out var outerField, out var outerWire)) return false;
        if (outerField != 1 || outerWire != 2) return false;
        return TryReadLengthDelimited(span, ref pos, out inner);
    }
}
