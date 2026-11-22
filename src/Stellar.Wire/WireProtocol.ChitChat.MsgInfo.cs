using System;

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
    /// can't be attributed to a conversation. 0 when absent. The remaining fields (multi_lang_notice,
    /// voice, chat_hypertext) are skipped.
    /// </summary>
    public static void TryParseChatMsgInfo(
        ReadOnlySpan<byte> data, out string text, out int msgType, out int pictureEmojiId, out long targetId)
    {
        text = string.Empty;
        msgType = 0;
        pictureEmojiId = 0;
        targetId = 0;
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out var field, out var wireType)) return;
            if (field == 1 && wireType == 0)
            {
                if (!TryReadVarint(data, ref pos, out var v)) return;
                msgType = (int)v;
            }
            else if (field == 2 && wireType == 0)
            {
                // target_id (int64 varint) — a charId, not an entity uuid.
                if (!TryReadVarint(data, ref pos, out var t)) return;
                targetId = unchecked((long)t);
            }
            else if (field == 3 && wireType == 2)
            {
                if (!TryReadString(data, ref pos, out text)) return;
            }
            else if (field == 5 && wireType == 2)
            {
                // picture_emoji (nested ChatPictureEmoji) — whole-message sticker. Read the
                // length-delimited sub-message, then pull its field 1 (config_id varint).
                if (!TryReadLengthDelimited(data, ref pos, out var emoji)) return;
                pictureEmojiId = ReadPictureEmojiConfigId(emoji);
            }
            else
            {
                if (!SkipField(data, ref pos, wireType)) return;
            }
        }
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
