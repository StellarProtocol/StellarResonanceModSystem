using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Wire;

/// <summary>
/// Pure protobuf parsing for the BPSR ChitChat wire format. Extracted from
/// <c>PandaChatProbe</c> so the logic can be unit-tested without loading any
/// IL2CPP / BepInEx / HarmonyX dependencies.
///
/// All methods are <c>static</c> and side-effect-free. Defensive Try* pattern
/// throughout — malformed input causes a short-circuit return, never an
/// exception. Only the fields actually projected to <see cref="ChatMessage"/>
/// are decoded; everything else is consumed via <see cref="SkipField"/> so
/// the parser stays robust to schema additions on the server side.
///
/// Wire layout reference: <c>(local reference)</c>
/// and <c>client.py</c>. Endianness is big-endian throughout.
///
/// The class is split across three partials by responsibility:
/// <list type="bullet">
///   <item><c>WireProtocol.cs</c> — envelope unwraps + wire-enum mappers (this file)</item>
///   <item><c>WireProtocol.Primitives.cs</c> — varint/tag/string/length-delim/skip readers</item>
///   <item><c>WireProtocol.ChitChat.cs</c> — ChitChat-family message parsers</item>
/// </list>
/// </summary>
public static partial class WireProtocol
{
    /// <summary>
    /// Unwrap the <c>ChitChat.GetChipChatRecords_Ret</c> envelope into its inner
    /// <c>GetChipChatRecordsReply</c> bytes. Per the proto, the envelope has a
    /// single field <c>ret = 1</c> (length-delimited) containing the reply.
    /// If the first tag isn't <c>(1, length-delimited)</c> or the inner length
    /// doesn't fit, returns <paramref name="payload"/> unchanged so the caller
    /// can attempt a direct-reply parse.
    ///
    /// Tag byte 0x0A = (field 1 &lt;&lt; 3) | wire-type 2 (length-delimited).
    /// Identical shape to <see cref="UnwrapNotifyEnvelope"/> — both are
    /// single-field protobuf wrappers around a request/reply struct.
    /// </summary>
    public static ReadOnlySpan<byte> UnwrapReturnEnvelope(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return payload;
        if (payload[0] != 0x0A) return payload;

        int pos = 1;
        if (!TryReadVarint(payload, ref pos, out var len)) return payload;
        if (len > int.MaxValue) return payload;
        int n = (int)len;
        if (pos + n > payload.Length) return payload;
        return payload.Slice(pos, n);
    }

    /// <summary>
    /// Unwrap the <c>ChitChatNtf.NotifyNewestChitChatMsgs</c> envelope into its
    /// inner <c>NotifyNewestChitChatMsgsRequest</c> bytes. Per the proto, the
    /// envelope has a single field <c>v_request = 1</c> (length-delimited)
    /// containing the request. If the first tag isn't <c>(1, length-delimited)</c>
    /// or the inner length doesn't fit, returns <paramref name="payload"/>
    /// unchanged so the caller can attempt a direct-request parse.
    ///
    /// Tag byte 0x0A = (field 1 &lt;&lt; 3) | wire-type 2 (length-delimited).
    /// </summary>
    public static ReadOnlySpan<byte> UnwrapNotifyEnvelope(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return payload;
        if (payload[0] != 0x0A) return payload;

        int pos = 1;
        if (!TryReadVarint(payload, ref pos, out var len)) return payload;
        if (len > int.MaxValue) return payload;
        int n = (int)len;
        if (pos + n > payload.Length) return payload;
        // Envelope unwrap looks valid — descend.
        return payload.Slice(pos, n);
    }

    /// <summary>
    /// Wire-channel int (Zproto.ChitChatChannelType) → ChatChannel projection.
    /// Mirrors <c>enum_chit_chat_channel_type.proto</c>.
    /// </summary>
    public static ChatChannel MapWireChannel(int wireValue) => wireValue switch
    {
        1  => ChatChannel.World,    // ChannelWorld
        2  => ChatChannel.Say,      // ChannelScene
        3  => ChatChannel.Party,    // ChannelTeam
        4  => ChatChannel.Guild,    // ChannelUnion
        5  => ChatChannel.Whisper,  // ChannelPrivate
        6  => ChatChannel.Guild,    // ChannelGroup — closest existing bucket
        7  => ChatChannel.System,   // ChannelTopNotice — system-style broadcast
        8  => ChatChannel.System,   // ChannelPlay — gameplay-broadcast bucket
        99 => ChatChannel.Whisper,  // per-target whisper-history channel on the SEA build (observed live; not 99=ChannelSystem in the BPSR-B proto)
        _  => ChatChannel.Unknown,  // ChannelNull (0) or unknown
    };

    /// <summary>
    /// Wire msg-type int (Zproto.ChitChatMsgType) → ChatMessageType projection.
    /// Mirrors <c>enum_chit_chat_msg_type.proto</c>:
    /// <list type="bullet">
    ///   <item>0 = ChatMsgTextMessage → Regular</item>
    ///   <item>1 = ChatMsgTextNotice → System</item>
    ///   <item>2 = ChatMsgMultiLangNotice → System</item>
    ///   <item>3 = ChatMsgPictureEmoji → Emote</item>
    ///   <item>4 = ChatMsgPicture → Unknown (no plugin-facing bucket yet)</item>
    ///   <item>5 = ChatMsgVoice → Unknown</item>
    ///   <item>6 = ChatMsgHypertext → ItemLink (hypertext = clickable item link)</item>
    /// </list>
    /// </summary>
    public static ChatMessageType MapWireMsgType(int wireValue) => wireValue switch
    {
        0 => ChatMessageType.Regular,
        1 => ChatMessageType.System,
        2 => ChatMessageType.System,
        3 => ChatMessageType.Emote,
        6 => ChatMessageType.ItemLink,
        _ => ChatMessageType.Unknown,
    };
}
