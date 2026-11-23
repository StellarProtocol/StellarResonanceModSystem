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
            Channel:    ChatChannel.Unknown, // channel implied by original request — overridden by ProcessChitChatReturnPayload via SendBytes call_id correlation
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
            IsHistory:  true)
        { StickerId = parsed.StickerId, TargetId = parsed.TargetId, Hypertext = parsed.Hypertext, Voice = parsed.Voice };
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
            MsgId:      fields.MsgId)
        { StickerId = fields.StickerId, TargetId = fields.TargetId, Hypertext = fields.Hypertext, Voice = fields.Voice };
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
            state.StickerId = parsedMsg.StickerId;
            state.TargetId = parsedMsg.TargetId;
            state.Hypertext = parsedMsg.Hypertext;
            state.Voice = parsedMsg.Voice;
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
        public int StickerId;
        public long TargetId;
        public ChatHypertext? Hypertext;
        public ChatVoice? Voice;
    }

    // ---------------------------------------------------------------------
    // ChitChatMsg
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parsed result from <see cref="TryParseChitChatMsg"/>.
    /// Public because <see cref="TryParseChitChatMsg"/> is a public plugin-facing API.
    /// <c>TargetId</c> = <c>ChatMsgInfo.target_id</c> (the whisper RECIPIENT's charId; 0 when absent).
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
        int MsgType,
        int StickerId,
        long TargetId)
    {
        /// <summary><c>ChatMsgInfo.chat_hypertext</c> raw slots (segments not built yet); null when absent.</summary>
        public ChatHypertext? Hypertext { get; init; }
        /// <summary><c>ChatMsgInfo.voice</c> (transcript unsanitised); null when absent.</summary>
        public ChatVoice? Voice { get; init; }
    }

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
            MsgType: fields.MsgType,
            StickerId: fields.StickerId,
            TargetId: fields.TargetId)
        { Hypertext = fields.Hypertext, Voice = fields.Voice };
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
            var mi = ParseChatMsgInfo(info);
            state.Text = mi.Text;
            state.MsgType = mi.MsgType;
            state.StickerId = mi.StickerId;
            state.TargetId = mi.TargetId;
            state.Hypertext = mi.Hypertext;
            state.Voice = mi.Voice;
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
        public int StickerId;
        public long TargetId;
        public ChatHypertext? Hypertext;
        public ChatVoice? Voice;
    }
}
