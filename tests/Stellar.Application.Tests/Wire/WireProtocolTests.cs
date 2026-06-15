using System;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

public sealed class WireProtocolTests
{
    // ============ TryReadVarint ============

    [Theory]
    [InlineData(new byte[] { 0x00 },               0UL)]
    [InlineData(new byte[] { 0x01 },               1UL)]
    [InlineData(new byte[] { 0x7F },               127UL)]
    [InlineData(new byte[] { 0x80, 0x01 },         128UL)]
    [InlineData(new byte[] { 0xAC, 0x02 },         300UL)]
    [InlineData(new byte[] { 0x8B, 0x88, 0x7A },   1_999_883UL)] // matches a real ChitChatMsg msg_id
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }, 0xFFFFFFFFUL)] // max uint32
    public void TryReadVarint_ValidEncodings_ReturnsValueAndAdvancesPos(byte[] bytes, ulong expected)
    {
        int pos = 0;
        var ok = WireProtocol.TryReadVarint(bytes, ref pos, out var value);

        Assert.True(ok);
        Assert.Equal(expected, value);
        Assert.Equal(bytes.Length, pos);
    }

    [Fact]
    public void TryReadVarint_TruncatedInput_ReturnsFalse()
    {
        // 0x80 has continuation bit set but there's no following byte.
        var bytes = new byte[] { 0x80 };
        int pos = 0;

        var ok = WireProtocol.TryReadVarint(bytes, ref pos, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryReadVarint_Overflow_ReturnsFalse()
    {
        // 11 bytes of 0xFF — exceeds the 10-byte varint limit (shift > 63).
        var bytes = Enumerable.Repeat((byte)0xFF, 11).ToArray();
        int pos = 0;

        var ok = WireProtocol.TryReadVarint(bytes, ref pos, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryReadVarint_EmptyInput_ReturnsFalse()
    {
        int pos = 0;
        var ok = WireProtocol.TryReadVarint(Array.Empty<byte>(), ref pos, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryReadVarint_StartsAtNonZeroPos_AdvancesCorrectly()
    {
        var bytes = new byte[] { 0xFF, 0x01, 0xAA }; // pos=1 → read 0x01 from offset 1
        int pos = 1;

        var ok = WireProtocol.TryReadVarint(bytes, ref pos, out var value);

        Assert.True(ok);
        Assert.Equal(1UL, value);
        Assert.Equal(2, pos);
    }

    // ============ TryReadTag ============

    [Theory]
    [InlineData(0x08, 1, 0)] // field=1 wire=varint
    [InlineData(0x0A, 1, 2)] // field=1 wire=length-delimited
    [InlineData(0x12, 2, 2)] // field=2 wire=length-delimited
    [InlineData(0x1D, 3, 5)] // field=3 wire=32-bit
    [InlineData(0x29, 5, 1)] // field=5 wire=64-bit
    public void TryReadTag_ValidSingleByte_DecodesFieldAndWireType(byte tagByte, int expectedField, int expectedWire)
    {
        var bytes = new[] { tagByte };
        int pos = 0;

        var ok = WireProtocol.TryReadTag(bytes, ref pos, out var field, out var wire);

        Assert.True(ok);
        Assert.Equal(expectedField, field);
        Assert.Equal(expectedWire, wire);
    }

    [Fact]
    public void TryReadTag_TruncatedInput_ReturnsFalse()
    {
        // Multi-byte tag with continuation bit but missing next byte.
        var bytes = new byte[] { 0x80 };
        int pos = 0;

        var ok = WireProtocol.TryReadTag(bytes, ref pos, out _, out _);

        Assert.False(ok);
    }

    // ============ TryReadString ============

    [Fact]
    public void TryReadString_ValidUtf8_ReturnsString()
    {
        // 0x05 = length 5; "hello" UTF-8
        var bytes = new byte[] { 0x05, 0x68, 0x65, 0x6C, 0x6C, 0x6F };
        int pos = 0;

        var ok = WireProtocol.TryReadString(bytes, ref pos, out var value);

        Assert.True(ok);
        Assert.Equal("hello", value);
        Assert.Equal(6, pos);
    }

    [Fact]
    public void TryReadString_ZeroLength_ReturnsEmpty()
    {
        var bytes = new byte[] { 0x00 };
        int pos = 0;

        var ok = WireProtocol.TryReadString(bytes, ref pos, out var value);

        Assert.True(ok);
        Assert.Equal(string.Empty, value);
        Assert.Equal(1, pos);
    }

    [Fact]
    public void TryReadString_TruncatedPayload_ReturnsFalse()
    {
        // 0x05 claims 5 bytes but only 3 follow.
        var bytes = new byte[] { 0x05, 0x68, 0x65, 0x6C };
        int pos = 0;

        var ok = WireProtocol.TryReadString(bytes, ref pos, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryReadString_NonAscii_DecodesCorrectly()
    {
        // Japanese: ミドクニ — typical sender name encountered in chat
        var name = "ミドクニ";
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var bytes = new byte[1 + nameBytes.Length];
        bytes[0] = (byte)nameBytes.Length;
        Array.Copy(nameBytes, 0, bytes, 1, nameBytes.Length);

        int pos = 0;
        var ok = WireProtocol.TryReadString(bytes, ref pos, out var value);

        Assert.True(ok);
        Assert.Equal(name, value);
    }

    // ============ TryReadLengthDelimited ============

    [Fact]
    public void TryReadLengthDelimited_ValidBytes_ReturnsSliceAndAdvances()
    {
        var bytes = new byte[] { 0x03, 0xAA, 0xBB, 0xCC, 0xFF };
        int pos = 0;

        var ok = WireProtocol.TryReadLengthDelimited(bytes, ref pos, out var inner);

        Assert.True(ok);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, inner.ToArray());
        Assert.Equal(4, pos);
    }

    [Fact]
    public void TryReadLengthDelimited_Truncated_ReturnsFalse()
    {
        var bytes = new byte[] { 0x05, 0xAA, 0xBB };
        int pos = 0;

        var ok = WireProtocol.TryReadLengthDelimited(bytes, ref pos, out _);

        Assert.False(ok);
    }

    // ============ SkipField ============

    [Fact]
    public void SkipField_Varint_AdvancesPastIt()
    {
        var bytes = new byte[] { 0xAC, 0x02, 0xFF }; // varint 300 + sentinel
        int pos = 0;

        var ok = WireProtocol.SkipField(bytes, ref pos, wireType: 0);

        Assert.True(ok);
        Assert.Equal(2, pos);
        Assert.Equal(0xFF, bytes[pos]);
    }

    [Fact]
    public void SkipField_LengthDelimited_AdvancesPastIt()
    {
        var bytes = new byte[] { 0x03, 0x01, 0x02, 0x03, 0xFF };
        int pos = 0;

        var ok = WireProtocol.SkipField(bytes, ref pos, wireType: 2);

        Assert.True(ok);
        Assert.Equal(4, pos);
        Assert.Equal(0xFF, bytes[pos]);
    }

    [Fact]
    public void SkipField_Fixed32_Advances4Bytes()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xFF };
        int pos = 0;

        var ok = WireProtocol.SkipField(bytes, ref pos, wireType: 5);

        Assert.True(ok);
        Assert.Equal(4, pos);
    }

    [Fact]
    public void SkipField_Fixed64_Advances8Bytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 0xFF };
        int pos = 0;

        var ok = WireProtocol.SkipField(bytes, ref pos, wireType: 1);

        Assert.True(ok);
        Assert.Equal(8, pos);
    }

    [Theory]
    [InlineData(3)]  // legacy group start
    [InlineData(4)]  // legacy group end
    [InlineData(6)]  // reserved
    public void SkipField_UnknownWireType_ReturnsFalse(int wireType)
    {
        var bytes = new byte[] { 0x01, 0x02 };
        int pos = 0;

        var ok = WireProtocol.SkipField(bytes, ref pos, wireType);

        Assert.False(ok);
    }

    // ============ MapWireChannel ============

    [Theory]
    [InlineData(1, ChatChannel.World)]
    [InlineData(2, ChatChannel.Say)]
    [InlineData(3, ChatChannel.Party)]
    [InlineData(4, ChatChannel.Guild)]
    [InlineData(5, ChatChannel.Whisper)]   // ChannelPrivate — sent whispers / cross-target whisper inbox
    [InlineData(7, ChatChannel.System)]    // ChannelTopNotice
    [InlineData(8, ChatChannel.System)]    // ChannelPlay
    [InlineData(99, ChatChannel.Whisper)]  // SEA-build per-target whisper history (observed live)
    public void MapWireChannel_KnownWireValue_MapsCorrectly(int wireValue, ChatChannel expected)
        => Assert.Equal(expected, WireProtocol.MapWireChannel(wireValue));

    [Fact]
    public void MapWireChannel_BothWhisperEncodings_MapToWhisper()
    {
        // Live capture proves the SEA build uses two channel ints for whispers:
        //  - 5 (ChannelPrivate)            — sent whispers / cross-target inbox
        //  - 99 (per-target whisper history) — fired when a friend tab opens
        // Both must project to ChatChannel.Whisper so the renderer tags them
        // [Whisp], not [Sys].
        Assert.Equal(ChatChannel.Whisper, WireProtocol.MapWireChannel(5));
        Assert.Equal(ChatChannel.Whisper, WireProtocol.MapWireChannel(99));
    }

    [Theory]
    [InlineData(0)]    // ChannelNull
    [InlineData(42)]   // unmapped
    [InlineData(-1)]   // bogus
    public void MapWireChannel_UnknownValue_ReturnsUnknown(int wireValue)
        => Assert.Equal(ChatChannel.Unknown, WireProtocol.MapWireChannel(wireValue));

    // ============ MapWireMsgType ============

    [Theory]
    [InlineData(0, ChatMessageType.Regular)]   // ChatMsgTextMessage
    [InlineData(1, ChatMessageType.System)]    // ChatMsgTextNotice
    [InlineData(2, ChatMessageType.System)]    // ChatMsgMultiLangNotice
    [InlineData(3, ChatMessageType.Emote)]     // ChatMsgPictureEmoji
    [InlineData(6, ChatMessageType.ItemLink)]  // ChatMsgHypertext
    public void MapWireMsgType_KnownValue_MapsCorrectly(int wireValue, ChatMessageType expected)
        => Assert.Equal(expected, WireProtocol.MapWireMsgType(wireValue));

    [Theory]
    [InlineData(4)] // ChatMsgPicture — no plugin bucket
    [InlineData(5)] // ChatMsgVoice
    [InlineData(99)] // bogus
    public void MapWireMsgType_UnmappedValue_ReturnsUnknown(int wireValue)
        => Assert.Equal(ChatMessageType.Unknown, WireProtocol.MapWireMsgType(wireValue));

    // ============ UnwrapNotifyEnvelope ============

    [Fact]
    public void UnwrapNotifyEnvelope_WellFormed_ReturnsInnerSlice()
    {
        // Envelope: field 1 length-delimited wrapping bytes [0xAA 0xBB 0xCC]
        var inner = new byte[] { 0xAA, 0xBB, 0xCC };
        var bytes = new WireBytes().Tag(1, 2).LengthDelimited(inner).ToArray();

        var result = WireProtocol.UnwrapNotifyEnvelope(bytes);

        Assert.Equal(inner, result.ToArray());
    }

    [Fact]
    public void UnwrapNotifyEnvelope_NoEnvelope_FallsThrough()
    {
        // First tag isn't (1, length-delimited) so helper returns input unchanged.
        var bytes = new byte[] { 0x08, 0x01 }; // field 1 varint = 1
        var result = WireProtocol.UnwrapNotifyEnvelope(bytes);
        Assert.Equal(bytes, result.ToArray());
    }

    [Fact]
    public void UnwrapNotifyEnvelope_TruncatedLength_FallsThrough()
    {
        // Tag says length-delimited but the length varint is truncated.
        var bytes = new byte[] { 0x0A };
        var result = WireProtocol.UnwrapNotifyEnvelope(bytes);
        Assert.Equal(bytes, result.ToArray());
    }

    [Fact]
    public void UnwrapNotifyEnvelope_LengthExceedsBuffer_FallsThrough()
    {
        // Claims 100 bytes inner but only 3 available.
        var bytes = new byte[] { 0x0A, 0x64, 0x01, 0x02, 0x03 };
        var result = WireProtocol.UnwrapNotifyEnvelope(bytes);
        Assert.Equal(bytes, result.ToArray());
    }

    // ============ UnwrapReturnEnvelope ============

    [Fact]
    public void UnwrapReturnEnvelope_WellFormed_ReturnsInnerSlice()
    {
        var inner = new byte[] { 0x10, 0x20 };
        var bytes = new WireBytes().Tag(1, 2).LengthDelimited(inner).ToArray();

        var result = WireProtocol.UnwrapReturnEnvelope(bytes);

        Assert.Equal(inner, result.ToArray());
    }

    [Fact]
    public void UnwrapReturnEnvelope_TooShort_FallsThrough()
    {
        var bytes = new byte[] { 0x0A };
        var result = WireProtocol.UnwrapReturnEnvelope(bytes);
        Assert.Equal(bytes, result.ToArray());
    }

    // ============ TryParseChitChatMsg ============

    [Fact]
    public void TryParseChitChatMsg_WithAllFields_DecodesCorrectly()
    {
        // ChitChatMsg shape:
        //   field 1 = msg_id (varint)
        //   field 2 = send_char_info (BasicShowInfo, length-delimited)
        //   field 3 = timestamp (varint)
        //   field 4 = msg_info (ChatMsgInfo, length-delimited)
        var basicShowInfo = new WireBytes()
            .Tag(1, 0).Varint(123456)         // char_id
            .Tag(2, 2).String("Ribery")        // name
            .Tag(3, 0).Varint(2)               // gender = female
            .Tag(5, 0).Varint(60)              // level
            .Tag(8, 0).Varint(1)               // is_newbie
            .ToArray();

        var msgInfo = new WireBytes()
            .Tag(1, 0).Varint(0)               // msg_type = ChatMsgTextMessage
            .Tag(3, 2).String("hello world")   // msg_text
            .ToArray();

        var chitChatMsg = new WireBytes()
            .Tag(1, 0).Varint(1_999_883)       // msg_id
            .Tag(2, 2).LengthDelimited(basicShowInfo)
            .Tag(3, 0).Varint(1_779_370_145)   // timestamp
            .Tag(4, 2).LengthDelimited(msgInfo)
            .ToArray();

        var ok = WireProtocol.TryParseChitChatMsg(chitChatMsg, out var r);

        Assert.True(ok);
        Assert.Equal(1_999_883L, r.MsgId);
        Assert.Equal("Ribery", r.SenderName);
        Assert.Equal(123456L, r.SenderId);
        Assert.Equal(2, r.SenderGender);
        Assert.Equal(60, r.SenderLevel);
        Assert.True(r.SenderIsNewbie);
        Assert.Equal(1_779_370_145L, r.Timestamp);
        Assert.Equal("hello world", r.Text);
        Assert.Equal(0, r.MsgType);
    }

    [Fact]
    public void TryParseChitChatMsg_OnlyMsgId_DecodesMsgIdAndDefaultsRest()
    {
        var bytes = new WireBytes().Tag(1, 0).Varint(42).ToArray();

        var ok = WireProtocol.TryParseChitChatMsg(bytes, out var r);

        Assert.True(ok);
        Assert.Equal(42L, r.MsgId);
        Assert.Equal(string.Empty, r.SenderName);
        Assert.Equal(0L, r.SenderId);
        Assert.Equal(0, r.SenderGender);
        Assert.Equal(0, r.SenderLevel);
        Assert.False(r.SenderIsNewbie);
        Assert.Equal(0L, r.Timestamp);
        Assert.Equal(string.Empty, r.Text);
        Assert.Equal(0, r.MsgType);
    }

    [Fact]
    public void TryParseChitChatMsg_UnknownField_IsSkipped()
    {
        // Field 7 isn't in the proto we care about; parser should SkipField it.
        var bytes = new WireBytes()
            .Tag(1, 0).Varint(99)
            .Tag(7, 0).Varint(12345)
            .ToArray();

        var ok = WireProtocol.TryParseChitChatMsg(bytes, out var r);

        Assert.True(ok);
        Assert.Equal(99L, r.MsgId);
    }

    // ============ TryParseChitChatNotify ============

    [Fact]
    public void TryParseChitChatNotify_RealWorldShape_BuildsChatMessage()
    {
        // NotifyNewestChitChatMsgsRequest:
        //   field 1 = channel_type (varint enum)
        //   field 2 = chat_msg (ChitChatMsg, length-delimited)
        var basicShowInfo = new WireBytes()
            .Tag(1, 0).Varint(5532317)
            .Tag(2, 2).String("RoseArcNelia")
            .Tag(3, 0).Varint(2)
            .Tag(5, 0).Varint(60)
            .ToArray();

        var msgInfo = new WireBytes()
            .Tag(1, 0).Varint(0)
            .Tag(3, 2).String("kecuali suami w")
            .ToArray();

        var chitChatMsg = new WireBytes()
            .Tag(1, 0).Varint(1_999_879)
            .Tag(2, 2).LengthDelimited(basicShowInfo)
            .Tag(3, 0).Varint(1_779_370_138)
            .Tag(4, 2).LengthDelimited(msgInfo)
            .ToArray();

        var notifyRequest = new WireBytes()
            .Tag(1, 0).Varint(1)                          // channel_type = ChannelWorld
            .Tag(2, 2).LengthDelimited(chitChatMsg)
            .ToArray();

        var ok = WireProtocol.TryParseChitChatNotify(notifyRequest, out var msg, out var msgId);

        Assert.True(ok);
        Assert.NotNull(msg);
        Assert.Equal(1_999_879L, msgId);
        Assert.Equal(ChatChannel.World, msg!.Channel);
        Assert.Equal("RoseArcNelia", msg.SenderName);
        Assert.Equal(5532317L, msg.SenderId);
        Assert.Equal("kecuali suami w", msg.Text);
        Assert.Equal(ChatMessageType.Regular, msg.Type);
        Assert.Equal(1_999_879L, msg.MsgId);
        Assert.NotNull(msg.Sender);
        Assert.Equal(60, msg.Sender!.Value.Level);
        Assert.Equal(2, msg.Sender!.Value.Gender);
    }

    [Fact]
    public void TryParseChitChatNotify_NoChatMsg_StillReturnsTrueWithChannelOnly()
    {
        // Channel only — no nested chat_msg.
        var bytes = new WireBytes().Tag(1, 0).Varint(3).ToArray(); // channel_type=Team

        var ok = WireProtocol.TryParseChitChatNotify(bytes, out var msg, out var msgId);

        Assert.True(ok);
        Assert.NotNull(msg);
        Assert.Equal(0L, msgId);
        Assert.Equal(ChatChannel.Party, msg!.Channel);
        Assert.Equal(string.Empty, msg.SenderName);
    }

    // ============ TryParseGetChipChatRecordsReply ============

    [Fact]
    public void TryParseGetChipChatRecordsReply_MultipleMessages_ReturnsAll()
    {
        // Build two ChitChatMsgs inline in field 5 (multi_msg_list).
        var msg1 = new WireBytes()
            .Tag(1, 0).Varint(100)
            .Tag(2, 2).LengthDelimited(new WireBytes()
                .Tag(1, 0).Varint(10)
                .Tag(2, 2).String("alice")
                .ToArray())
            .Tag(4, 2).LengthDelimited(new WireBytes()
                .Tag(1, 0).Varint(0)
                .Tag(3, 2).String("hi")
                .ToArray())
            .ToArray();

        var msg2 = new WireBytes()
            .Tag(1, 0).Varint(101)
            .Tag(2, 2).LengthDelimited(new WireBytes()
                .Tag(1, 0).Varint(11)
                .Tag(2, 2).String("bob")
                .ToArray())
            .Tag(4, 2).LengthDelimited(new WireBytes()
                .Tag(1, 0).Varint(0)
                .Tag(3, 2).String("hey")
                .ToArray())
            .ToArray();

        // GetChipChatRecordsReply.multi_msg_list (field 5, repeated).
        var reply = new WireBytes()
            .Tag(5, 2).LengthDelimited(msg1)
            .Tag(5, 2).LengthDelimited(msg2)
            .Tag(3, 0).Varint(1)  // is_end = true (skipped by parser)
            .ToArray();

        var count = WireProtocol.TryParseGetChipChatRecordsReply(reply, out var messages);

        Assert.Equal(2, count);
        Assert.Equal(2, messages.Count);
        Assert.Equal(100L, messages[0].MsgId);
        Assert.Equal("alice", messages[0].Msg.SenderName);
        Assert.Equal("hi",    messages[0].Msg.Text);
        Assert.Equal(101L, messages[1].MsgId);
        Assert.Equal("bob",   messages[1].Msg.SenderName);
        Assert.Equal("hey",   messages[1].Msg.Text);
        // History messages don't carry channel_type on the wire.
        Assert.Equal(ChatChannel.Unknown, messages[0].Msg.Channel);
    }

    [Fact]
    public void TryParseGetChipChatRecordsReply_EmptyBatch_ReturnsZero()
    {
        // Reply with is_end + err_code but no multi_msg_list.
        var reply = new WireBytes()
            .Tag(3, 0).Varint(1)
            .Tag(6, 0).Varint(0)
            .ToArray();

        var count = WireProtocol.TryParseGetChipChatRecordsReply(reply, out var messages);

        Assert.Equal(0, count);
        Assert.Empty(messages);
    }

    [Fact]
    public void TryParseGetChipChatRecordsReply_NonChatReplyShape_ReturnsZero()
    {
        // A reply that happens to have nothing the parser recognizes — empty result.
        var reply = new WireBytes()
            .Tag(1, 0).Varint(42)
            .Tag(2, 2).String("not chat data")
            .ToArray();

        var count = WireProtocol.TryParseGetChipChatRecordsReply(reply, out var messages);

        Assert.Equal(0, count);
        Assert.Empty(messages);
    }

    // ============ ParseChannelTypeFromGetChipChatRecordsRequest ============

    [Fact]
    public void ParseChannelType_WellFormedEnvelope_ReturnsChannelType()
    {
        // ChitChat.GetChipChatRecords { v_request = 1 : GetChipChatRecordsRequest }
        //   where GetChipChatRecordsRequest has field 2 = channel_type (varint enum).
        var request = new WireBytes()
            .Tag(2, 0).Varint(1)     // channel_type = ChannelWorld
            .Tag(4, 0).Varint(30)    // record_num — ignored
            .ToArray();

        var envelope = new WireBytes()
            .Tag(1, 2).LengthDelimited(request)
            .ToArray();

        var result = WireProtocol.ParseChannelTypeFromGetChipChatRecordsRequest(envelope);

        Assert.Equal(1, result);
    }

    [Theory]
    [InlineData(1)]   // World
    [InlineData(2)]   // Scene
    [InlineData(3)]   // Team
    [InlineData(4)]   // Union
    [InlineData(99)]  // System
    public void ParseChannelType_RecognizesAllKnownChannels(int channelType)
    {
        var request = new WireBytes().Tag(2, 0).Varint((ulong)channelType).ToArray();
        var envelope = new WireBytes().Tag(1, 2).LengthDelimited(request).ToArray();

        var result = WireProtocol.ParseChannelTypeFromGetChipChatRecordsRequest(envelope);

        Assert.Equal(channelType, result);
    }

    [Fact]
    public void ParseChannelType_MalformedEnvelope_ReturnsMinusOne()
    {
        // Wrong outer field number.
        var bytes = new WireBytes().Tag(2, 2).LengthDelimited(new byte[] { 0x10, 0x01 }).ToArray();

        var result = WireProtocol.ParseChannelTypeFromGetChipChatRecordsRequest(bytes);

        Assert.Equal(-1, result);
    }

    [Fact]
    public void ParseChannelType_NoChannelTypeField_ReturnsMinusOne()
    {
        // Envelope is correct but the inner request has no field 2.
        var request = new WireBytes().Tag(3, 0).Varint(0).ToArray();
        var envelope = new WireBytes().Tag(1, 2).LengthDelimited(request).ToArray();

        var result = WireProtocol.ParseChannelTypeFromGetChipChatRecordsRequest(envelope);

        Assert.Equal(-1, result);
    }

    // ============ TryParseBasicShowInfo ============

    [Fact]
    public void TryParseBasicShowInfo_AllSurfacedFields_DecodesCorrectly()
    {
        var bytes = new WireBytes()
            .Tag(1, 0).Varint(98765)
            .Tag(2, 2).String("Karu")
            .Tag(3, 0).Varint(1)
            .Tag(5, 0).Varint(45)
            .Tag(8, 0).Varint(0)
            .ToArray();

        var r = WireProtocol.TryParseBasicShowInfo(bytes);

        Assert.Equal("Karu", r.Name);
        Assert.Equal(98765L, r.CharId);
        Assert.Equal(1, r.Gender);
        Assert.Equal(45, r.Level);
        Assert.False(r.IsNewbie);
    }

    [Fact]
    public void TryParseBasicShowInfo_UnsurfacedFieldsSkipped_NoError()
    {
        // body_size (4), cur_talent_pool_id (6), union_hunt_rand_idx (7), is_backflow (9)
        // are present but skipped — should not affect the decoded fields.
        var bytes = new WireBytes()
            .Tag(1, 0).Varint(11)
            .Tag(2, 2).String("X")
            .Tag(4, 0).Varint(2)        // body_size, skipped
            .Tag(5, 0).Varint(60)
            .Tag(6, 0).Varint(0)        // cur_talent_pool_id, skipped
            .Tag(7, 0).Varint(0)        // union_hunt_rand_idx, skipped
            .Tag(9, 0).Varint(0)        // is_backflow, skipped
            .ToArray();

        var r = WireProtocol.TryParseBasicShowInfo(bytes);

        Assert.Equal("X", r.Name);
        Assert.Equal(11L, r.CharId);
        Assert.Equal(60, r.Level);
    }

    // ============ TryParseChatMsgInfo ============

    [Fact]
    public void TryParseChatMsgInfo_TextMessage_DecodesText()
    {
        var bytes = new WireBytes()
            .Tag(1, 0).Varint(0)
            .Tag(3, 2).String("ping")
            .ToArray();

        WireProtocol.TryParseChatMsgInfo(bytes, out var text, out var msgType);

        Assert.Equal("ping", text);
        Assert.Equal(0, msgType);
    }

    [Fact]
    public void TryParseChatMsgInfo_Hypertext_DecodesType()
    {
        var bytes = new WireBytes()
            .Tag(1, 0).Varint(6)
            .Tag(3, 2).String("[item link]")
            .ToArray();

        WireProtocol.TryParseChatMsgInfo(bytes, out var text, out var msgType);

        Assert.Equal("[item link]", text);
        Assert.Equal(6, msgType);
    }

    [Fact]
    public void TryParseChatMsgInfo_OnlyMsgType_ReturnsEmptyText()
    {
        var bytes = new WireBytes().Tag(1, 0).Varint(3).ToArray();

        WireProtocol.TryParseChatMsgInfo(bytes, out var text, out var msgType);

        Assert.Equal(string.Empty, text);
        Assert.Equal(3, msgType);
    }
}
