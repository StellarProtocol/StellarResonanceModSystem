using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Wire;

/// <summary>
/// ChitChat-family protobuf parsers: <c>GetChipChatRecordsReply</c>,
/// <c>NotifyNewestChitChatMsgsRequest</c>, <c>ChitChatMsg</c>,
/// <c>BasicShowInfo</c>, <c>ChatMsgInfo</c>, and the
/// <c>GetChipChatRecordsRequest</c> envelope.
/// </summary>
public static partial class WireProtocol
{
    // ---------------------------------------------------------------------
    // GetChipChatRecordsReply (history fetch)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parse a <c>GetChipChatRecordsReply</c> into a list of
    /// <see cref="ChatMessage"/>. Returns the number of messages parsed
    /// (0 when the protobuf doesn't carry field 5, which is the signal that
    /// this Return wasn't a history-fetch reply).
    ///
    /// Reply shape:
    /// <code>
    ///   message GetChipChatRecordsReply {
    ///     bool is_end = 3;
    ///     int64 max_read_msg_id = 4;
    ///     repeated zproto.ChitChatMsg multi_msg_list = 5;
    ///     zproto.EErrorCode err_code = 6;
    ///   }
    /// </code>
    /// Each <c>multi_msg_list</c> entry is the same <c>ChitChatMsg</c> we already
    /// parse for live notifications via <see cref="TryParseChitChatMsg"/>.
    ///
    /// Historical messages don't carry a per-batch channel_type on the wire —
    /// channel is implied by the original <c>GetChipChatRecordsRequest</c>,
    /// which we don't observe from postfix-only hooks. We mark history channel
    /// as <see cref="ChatChannel.Unknown"/>; plugins that need per-channel
    /// history will need a richer request-tracking design.
    /// </summary>
    public static int TryParseGetChipChatRecordsReply(
        ReadOnlySpan<byte> payload,
        out List<(ChatMessage Msg, long MsgId)> messages)
    {
        messages = new List<(ChatMessage, long)>();
        int pos = 0;
        int count = 0;
        while (pos < payload.Length)
        {
            if (!TryReadTag(payload, ref pos, out var field, out var wireType)) return count;

            if (field == 5 && wireType == 2)
            {
                if (TryReadChitChatRecord(payload, ref pos, out var record))
                {
                    messages.Add(record);
                    count++;
                }
            }
            else
            {
                if (!SkipField(payload, ref pos, wireType)) return count;
            }
        }
        return count;
    }

    /// <summary>
    /// Read one repeated <c>multi_msg_list</c> entry (field 5) at
    /// <paramref name="pos"/>. Advances <paramref name="pos"/> past the entry
    /// even on partial extraction (protobuf semantics tolerate malformed
    /// sub-messages — the outer loop continues). Returns false only when the
    /// entry's length prefix can't be read or the sub-message can't be
    /// projected into a <see cref="ChatMessage"/>.
    /// </summary>
    private static bool TryReadChitChatRecord(
        ReadOnlySpan<byte> payload,
        ref int pos,
        out (ChatMessage Msg, long MsgId) record)
    {
        record = default;
        if (!TryReadLengthDelimited(payload, ref pos, out var msgBytes)) return false;
        if (!TryParseChitChatMsg(msgBytes, out var parsed))
            return false;

        var msg = new ChatMessage(
            Channel:    ChatChannel.Unknown, // channel implied by original request — overridden by ProcessChitChatReturn via ProxyCall correlation
            SenderName: parsed.SenderName,
            SenderId:   parsed.SenderId,
            Text:       parsed.Text,
            Timestamp:  parsed.Timestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(parsed.Timestamp).UtcDateTime
                : DateTime.UtcNow,
            Type:       MapWireMsgType(parsed.MsgType),
            RawPayload: ReadOnlyMemory<byte>.Empty,
            Sender:     new SenderMeta(parsed.SenderLevel, Job: null, Guild: null, Gender: parsed.SenderGender, IsNewbie: parsed.SenderIsNewbie),
            MsgId:      parsed.MsgId,
            IsHistory:  true);
        record = (msg, parsed.MsgId);
        return true;
    }

    // ---------------------------------------------------------------------
    // NotifyNewestChitChatMsgsRequest (live push)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parse a <c>NotifyNewestChitChatMsgsRequest</c> payload into a
    /// <see cref="ChatMessage"/>. Returns false on malformed/truncated input.
    /// </summary>
    public static bool TryParseChitChatNotify(ReadOnlySpan<byte> payload, out ChatMessage? msg, out long msgId)
    {
        msg = null;
        msgId = 0;
        var fields = new ChitChatNotifyState { SenderName = string.Empty, Text = string.Empty };
        int pos = 0;

        while (pos < payload.Length)
        {
            if (!TryReadTag(payload, ref pos, out var field, out var wireType)) return false;
            if (!ReadChitChatNotifyFields(payload, ref pos, ref fields, field, wireType)) return false;
        }

        msgId = fields.MsgId;
        msg = new ChatMessage(
            Channel:    MapWireChannel(fields.ChannelType),
            SenderName: fields.SenderName,
            SenderId:   fields.SenderId,
            Text:       fields.Text,
            Timestamp:  fields.Timestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(fields.Timestamp).UtcDateTime
                : DateTime.UtcNow,
            Type:       MapWireMsgType(fields.MsgTypeWire),
            RawPayload: ReadOnlyMemory<byte>.Empty,
            Sender:     new SenderMeta(fields.SenderLevel, Job: null, Guild: null, Gender: fields.SenderGender, IsNewbie: fields.SenderIsNewbie),
            MsgId:      fields.MsgId);
        return true;
    }

    /// <summary>
    /// Per-tag handler for <see cref="TryParseChitChatNotify"/>. Reads the value
    /// for <paramref name="field"/>/<paramref name="wireType"/> and projects it
    /// onto <paramref name="state"/>. Returns false on malformed input.
    /// </summary>
    private static bool ReadChitChatNotifyFields(
        ReadOnlySpan<byte> payload,
        ref int pos,
        ref ChitChatNotifyState state,
        int field,
        int wireType)
    {
        if (field == 1 && wireType == 0)
        {
            // channel_type (varint enum)
            if (!TryReadVarint(payload, ref pos, out var v)) return false;
            state.ChannelType = (int)v;
            return true;
        }
        if (field == 2 && wireType == 2)
        {
            // chat_msg (nested ChitChatMsg)
            if (!TryReadLengthDelimited(payload, ref pos, out var chatMsgBytes)) return false;
            if (!TryParseChitChatMsg(chatMsgBytes, out var parsedMsg))
                return false;
            state.MsgId = parsedMsg.MsgId;
            state.SenderName = parsedMsg.SenderName;
            state.SenderId = parsedMsg.SenderId;
            state.SenderGender = parsedMsg.SenderGender;
            state.SenderLevel = parsedMsg.SenderLevel;
            state.SenderIsNewbie = parsedMsg.SenderIsNewbie;
            state.Timestamp = parsedMsg.Timestamp;
            state.Text = parsedMsg.Text;
            state.MsgTypeWire = parsedMsg.MsgType;
            return true;
        }
        return SkipField(payload, ref pos, wireType);
    }

    /// <summary>
    /// Mutable state holder threaded through <see cref="ReadChitChatNotifyFields"/>.
    /// <c>ref struct</c> keeps it on the stack — no boxing, no heap allocation.
    /// </summary>
    private ref struct ChitChatNotifyState
    {
        public int ChannelType;
        public long MsgId;
        public string SenderName;
        public long SenderId;
        public int SenderGender;
        public int SenderLevel;
        public bool SenderIsNewbie;
        public long Timestamp;
        public string Text;
        public int MsgTypeWire;
    }

    // ---------------------------------------------------------------------
    // ChitChatMsg
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parsed result from <see cref="TryParseChitChatMsg"/>.
    /// Public because <see cref="TryParseChitChatMsg"/> is a public plugin-facing API.
    /// </summary>
    public readonly record struct ChitChatMsgResult(
        long MsgId,
        string SenderName,
        long SenderId,
        int SenderGender,
        int SenderLevel,
        bool SenderIsNewbie,
        long Timestamp,
        string Text,
        int MsgType);

    /// <summary>
    /// Parse a <c>ChitChatMsg</c> sub-message. Returns true even on partial
    /// extraction — protobuf semantics are tolerant of missing fields.
    /// </summary>
    public static bool TryParseChitChatMsg(
        ReadOnlySpan<byte> data,
        out ChitChatMsgResult result)
    {
        var fields = new ChitChatMsgState { SenderName = string.Empty, Text = string.Empty };
        int pos = 0;

        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out var field, out var wireType))
            {
                result = default;
                return false;
            }
            if (!ReadChitChatMsgFields(data, ref pos, ref fields, field, wireType))
            {
                result = default;
                return false;
            }
        }

        result = new ChitChatMsgResult(
            MsgId: fields.MsgId,
            SenderName: fields.SenderName,
            SenderId: fields.SenderId,
            SenderGender: fields.SenderGender,
            SenderLevel: fields.SenderLevel,
            SenderIsNewbie: fields.SenderIsNewbie,
            Timestamp: fields.Timestamp,
            Text: fields.Text,
            MsgType: fields.MsgType);
        return true;
    }

    /// <summary>
    /// Per-tag handler for <see cref="TryParseChitChatMsg"/>. Reads the value
    /// for <paramref name="field"/>/<paramref name="wireType"/> and projects it
    /// onto <paramref name="state"/>. Returns false on malformed input.
    /// </summary>
    private static bool ReadChitChatMsgFields(
        ReadOnlySpan<byte> data,
        ref int pos,
        ref ChitChatMsgState state,
        int field,
        int wireType)
    {
        if (field == 1 && wireType == 0)
        {
            // msg_id (int64) — global unique message id; used for cross-path
            // deduplication between live Notify and history Return paths.
            if (!TryReadVarint(data, ref pos, out var v)) return false;
            state.MsgId = unchecked((long)v);
            return true;
        }
        if (field == 2 && wireType == 2)
        {
            // send_char_info (nested BasicShowInfo)
            if (!TryReadLengthDelimited(data, ref pos, out var info)) return false;
            var bsi = TryParseBasicShowInfo(info);
            state.SenderName = bsi.Name;
            state.SenderId = bsi.CharId;
            state.SenderGender = bsi.Gender;
            state.SenderLevel = bsi.Level;
            state.SenderIsNewbie = bsi.IsNewbie;
            return true;
        }
        if (field == 3 && wireType == 0)
        {
            if (!TryReadVarint(data, ref pos, out var ts)) return false;
            state.Timestamp = unchecked((long)ts);
            return true;
        }
        if (field == 4 && wireType == 2)
        {
            // msg_info (nested ChatMsgInfo)
            if (!TryReadLengthDelimited(data, ref pos, out var info)) return false;
            TryParseChatMsgInfo(info, out state.Text, out state.MsgType);
            return true;
        }
        return SkipField(data, ref pos, wireType);
    }

    /// <summary>
    /// Mutable state holder threaded through <see cref="ReadChitChatMsgFields"/>.
    /// <c>ref struct</c> keeps it on the stack — no boxing, no heap allocation.
    /// </summary>
    private ref struct ChitChatMsgState
    {
        public long MsgId;
        public string SenderName;
        public long SenderId;
        public int SenderGender;
        public int SenderLevel;
        public bool SenderIsNewbie;
        public long Timestamp;
        public string Text;
        public int MsgType;
    }

    // ---------------------------------------------------------------------
    // BasicShowInfo
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parsed result from <see cref="TryParseBasicShowInfo"/>.
    /// Public because <see cref="TryParseBasicShowInfo"/> is a public plugin-facing API.
    /// </summary>
    public readonly record struct BasicShowInfoResult(
        string Name,
        long CharId,
        int Gender,
        int Level,
        bool IsNewbie);

    /// <summary>
    /// Parse <c>BasicShowInfo</c> per <c>stru_basic_show_info.proto</c>: extracts
    /// char_id (1), name (2), gender (3), level (5), is_newbie (8). body_size,
    /// talent pool, union hunt idx, and is_backflow are skipped (no plugin
    /// consumers yet).
    /// </summary>
    public static BasicShowInfoResult TryParseBasicShowInfo(ReadOnlySpan<byte> data)
    {
        var fields = new BasicShowInfoState { Name = string.Empty };
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out var field, out var wireType)) break;
            if (!ReadBasicShowInfoFields(data, ref pos, ref fields, field, wireType)) break;
        }
        return new BasicShowInfoResult(
            Name: fields.Name,
            CharId: fields.CharId,
            Gender: fields.Gender,
            Level: fields.Level,
            IsNewbie: fields.IsNewbie);
    }

    /// <summary>
    /// Per-tag handler for <see cref="TryParseBasicShowInfo"/>. Returns false on
    /// malformed input — the outer loop short-circuits and preserves whatever
    /// fields were extracted before the failure (matches the original method's
    /// tolerant semantics).
    /// </summary>
    private static bool ReadBasicShowInfoFields(
        ReadOnlySpan<byte> data,
        ref int pos,
        ref BasicShowInfoState state,
        int field,
        int wireType)
    {
        if (field == 1 && wireType == 0)
        {
            if (!TryReadVarint(data, ref pos, out var v)) return false;
            state.CharId = unchecked((long)v);
            return true;
        }
        if (field == 2 && wireType == 2)
        {
            if (!TryReadString(data, ref pos, out state.Name)) return false;
            return true;
        }
        if (field == 3 && wireType == 0)
        {
            if (!TryReadVarint(data, ref pos, out var v)) return false;
            state.Gender = (int)v;
            return true;
        }
        if (field == 5 && wireType == 0)
        {
            if (!TryReadVarint(data, ref pos, out var v)) return false;
            state.Level = (int)v;
            return true;
        }
        if (field == 8 && wireType == 0)
        {
            if (!TryReadVarint(data, ref pos, out var v)) return false;
            state.IsNewbie = v != 0;
            return true;
        }
        return SkipField(data, ref pos, wireType);
    }

    /// <summary>
    /// Mutable state holder threaded through <see cref="ReadBasicShowInfoFields"/>.
    /// </summary>
    private ref struct BasicShowInfoState
    {
        public string Name;
        public long CharId;
        public int Gender;
        public int Level;
        public bool IsNewbie;
    }

    // ---------------------------------------------------------------------
    // ChatMsgInfo
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parse the <c>msg_type</c> + <c>msg_text</c> fields out of a
    /// <c>ChatMsgInfo</c>. Other fields (multi_lang_notice, picture_emoji,
    /// voice, chat_hypertext) are skipped — projecting them belongs to a later
    /// iteration if/when plugins need them.
    /// </summary>
    public static void TryParseChatMsgInfo(ReadOnlySpan<byte> data, out string text, out int msgType)
    {
        text = string.Empty;
        msgType = 0;
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out var field, out var wireType)) return;
            if (field == 1 && wireType == 0)
            {
                if (!TryReadVarint(data, ref pos, out var v)) return;
                msgType = (int)v;
            }
            else if (field == 3 && wireType == 2)
            {
                if (!TryReadString(data, ref pos, out text)) return;
            }
            else
            {
                if (!SkipField(data, ref pos, wireType)) return;
            }
        }
    }

    // ---------------------------------------------------------------------
    // GetChipChatRecordsRequest envelope parser
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parse the outer <c>ChitChat.GetChipChatRecords { v_request = 1 : ... }</c>
    /// envelope and pull the inner <c>GetChipChatRecordsRequest.channel_type</c>
    /// (field 2, varint enum). Returns -1 on shape mismatch.
    /// </summary>
    public static int ParseChannelTypeFromGetChipChatRecordsRequest(byte[] body)
    {
        var span = new ReadOnlySpan<byte>(body);
        int pos = 0;

        // Outer envelope: field 1 (v_request, length-delimited).
        if (!TryReadTag(span, ref pos, out var outerField, out var outerWire)) return -1;
        if (outerField != 1 || outerWire != 2) return -1;
        if (!TryReadLengthDelimited(span, ref pos, out var inner)) return -1;

        int ipos = 0;
        while (ipos < inner.Length)
        {
            if (!TryReadTag(inner, ref ipos, out var f, out var w)) return -1;
            if (f == 2 && w == 0)
            {
                if (!TryReadVarint(inner, ref ipos, out var v)) return -1;
                return (int)v;
            }
            if (!SkipField(inner, ref ipos, w)) return -1;
        }
        return -1;
    }
}
