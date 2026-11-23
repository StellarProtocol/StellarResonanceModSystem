using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Wire;

/// <summary>
/// <c>ChatMsgInfo</c> field parsers (text, sticker, whisper target) and the <c>GetChipChatRecordsRequest</c>
/// envelope parser. Split out of <c>WireProtocol.ChitChat.cs</c> to stay under the 500-LoC file cap.
/// </summary>
public static partial class WireProtocol
{
    // ---------------------------------------------------------------------
    // ChatMsgInfo  (BasicShowInfo parser → WireProtocol.BasicShowInfo.cs)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Parse the <c>msg_type</c>, <c>msg_text</c>, and <c>picture_emoji.config_id</c>
    /// (whole-message sticker) fields out of a <c>ChatMsgInfo</c>. Overload kept for
    /// callers that don't need the whisper recipient — see the 5-arg overload.
    /// </summary>
    public static void TryParseChatMsgInfo(ReadOnlySpan<byte> data, out string text, out int msgType, out int pictureEmojiId)
        => TryParseChatMsgInfo(data, out text, out msgType, out pictureEmojiId, out _);

    /// <summary>
    /// Parse <c>msg_type</c> (1), <c>target_id</c> (2), <c>msg_text</c> (3) and
    /// <c>picture_emoji.config_id</c> (5) out of a <c>ChatMsgInfo</c>. <paramref name="targetId"/> is the
    /// whisper RECIPIENT's charId (the game's peer rule, <c>chat_main_vm.lua:796-807</c>: the peer is the
    /// sender, or <c>msgInfo.targetId</c> when the sender is self) — without it our own whisper echoes
    /// can't be attributed to a conversation. 0 when absent. Voice / hypertext are decoded but dropped
    /// here — use <see cref="ParseChatMsgInfo"/> for them.
    /// </summary>
    public static void TryParseChatMsgInfo(
        ReadOnlySpan<byte> data, out string text, out int msgType, out int pictureEmojiId, out long targetId)
    {
        var f = ParseChatMsgInfo(data);
        text = f.Text;
        msgType = f.MsgType;
        pictureEmojiId = f.StickerId;
        targetId = f.TargetId;
    }

    /// <summary>Everything <see cref="ParseChatMsgInfo"/> reads out of one <c>ChatMsgInfo</c>.</summary>
    public readonly record struct ChatMsgInfoFields
    {
        /// <summary><c>msg_text</c> (3), verbatim; empty when absent.</summary>
        public string Text { get; init; }
        /// <summary><c>msg_type</c> (1) — the wire <c>ChitChatMsgType</c>.</summary>
        public int MsgType { get; init; }
        /// <summary><c>picture_emoji.config_id</c> (5.1); 0 when not a sticker.</summary>
        public int StickerId { get; init; }
        /// <summary><c>target_id</c> (2) — the whisper recipient's charId; 0 when absent.</summary>
        public long TargetId { get; init; }
        /// <summary><c>chat_hypertext</c> (7), raw slots only (segments are built by the Application layer on the
        /// main thread, where the game tables are); null when absent.</summary>
        public ChatHypertext? Hypertext { get; init; }
        /// <summary><c>voice</c> (6), transcript unsanitised; null when absent.</summary>
        public ChatVoice? Voice { get; init; }
    }

    /// <summary>
    /// Parse a <c>ChatMsgInfo</c>: <c>msg_type</c> (1), <c>target_id</c> (2), <c>msg_text</c> (3),
    /// <c>picture_emoji</c> (5), <c>voice</c> (6) and <c>chat_hypertext</c> (7). Never throws: a malformed tag
    /// stops the walk and keeps whatever was read before it (protobuf tolerance), and a malformed nested
    /// voice/hypertext body yields null / the slots read so far. <c>multi_lang_notice</c> (4) is skipped — the
    /// game itself only shows <c>msg_text</c> for it.
    /// </summary>
    public static ChatMsgInfoFields ParseChatMsgInfo(ReadOnlySpan<byte> data)
    {
        var s = new ChatMsgInfoFields { Text = string.Empty };
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out var field, out var wireType)) break;
            if (!ReadChatMsgInfoField(data, ref pos, ref s, field, wireType)) break;
        }
        return s;
    }

    // One ChatMsgInfo tag. Returns false on a truncated/malformed value (the caller stops the walk).
    private static bool ReadChatMsgInfoField(
        ReadOnlySpan<byte> data, ref int pos, ref ChatMsgInfoFields s, int field, int wireType)
    {
        if (wireType == 0 && field is 1 or 2)
        {
            if (!TryReadVarint(data, ref pos, out var v)) return false;
            // target_id (2) is an int64 charId, not an entity uuid.
            s = field == 1 ? s with { MsgType = (int)v } : s with { TargetId = unchecked((long)v) };
            return true;
        }
        if (wireType != 2 || field is not (3 or 5 or 6 or 7)) return SkipField(data, ref pos, wireType);
        if (!TryReadLengthDelimited(data, ref pos, out var body)) return false;
        s = field switch
        {
            3 => s with { Text = System.Text.Encoding.UTF8.GetString(body) },
            // picture_emoji (nested ChatPictureEmoji) — whole-message sticker; its field 1 = config_id.
            5 => s with { StickerId = ReadPictureEmojiConfigId(body) },
            6 => s with { Voice = ParseChatVoice(body) },
            _ => s with { Hypertext = ParseChatHypertext(body) },
        };
        return true;
    }

    /// <summary>
    /// Read <c>ChatPictureEmoji.config_id</c> (field 1, varint) out of a picture_emoji
    /// sub-message. Returns 0 when field 1 is absent or the body is malformed.
    /// </summary>
    private static int ReadPictureEmojiConfigId(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out var field, out var wireType)) return 0;
            if (field == 1 && wireType == 0)
            {
                if (!TryReadVarint(data, ref pos, out var v)) return 0;
                return (int)v;
            }
            if (!SkipField(data, ref pos, wireType)) return 0;
        }
        return 0;
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
        => ParseChannelTypeFromGetChipChatRecordsRequest(new ReadOnlySpan<byte>(body));

    /// <summary>
    /// Span overload of <see cref="ParseChannelTypeFromGetChipChatRecordsRequest(byte[])"/>.
    /// Lets the outbound send hook read <c>channel_type</c> straight out of the
    /// framed wire packet (slicing past the 26-byte Call header) with no
    /// sub-array allocation. Identical logic; returns -1 on shape mismatch.
    /// </summary>
    public static int ParseChannelTypeFromGetChipChatRecordsRequest(ReadOnlySpan<byte> span)
    {
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
