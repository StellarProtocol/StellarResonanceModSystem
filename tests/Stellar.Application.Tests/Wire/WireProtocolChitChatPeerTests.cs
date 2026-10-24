using System;
using Stellar.Abstractions.Domain;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>Whisper peer identity on the wire: <c>ChatMsgInfo.target_id</c> (field 2) and the private
/// history request's <c>GetChipChatRecordsRequest.target_id</c> (field 5).</summary>
public sealed class WireProtocolChitChatPeerTests
{
    // ============ ChatMsgInfo.target_id ============

    [Fact]
    public void TryParseChatMsgInfo_TargetIdField2_Decoded()
    {
        var bytes = new WireBytes()
            .Tag(1, 0).Varint(0)
            .Tag(2, 0).Varint(5_532_317_001UL)   // target_id > int32 range — must stay exact
            .Tag(3, 2).String("hi")
            .ToArray();

        WireProtocol.TryParseChatMsgInfo(bytes, out var text, out var msgType, out var sticker, out var targetId);

        Assert.Equal("hi", text);
        Assert.Equal(0, msgType);
        Assert.Equal(0, sticker);
        Assert.Equal(5_532_317_001L, targetId);
    }

    [Fact]
    public void TryParseChatMsgInfo_NoTargetId_IsZero()
    {
        var bytes = new WireBytes().Tag(1, 0).Varint(0).Tag(3, 2).String("x").ToArray();

        WireProtocol.TryParseChatMsgInfo(bytes, out _, out _, out _, out var targetId);

        Assert.Equal(0L, targetId);
    }

    [Fact]
    public void TryParseChitChatNotify_WhisperEcho_CarriesTargetId()
    {
        var notify = new WireBytes()
            .Tag(1, 0).Varint(5)                                   // channel_type = ChannelPrivate
            .Tag(2, 2).LengthDelimited(ChitChatMsg(senderId: 111, targetId: 222))
            .ToArray();

        Assert.True(WireProtocol.TryParseChitChatNotify(notify, out var msg, out _));

        Assert.NotNull(msg);
        Assert.Equal(ChatChannel.Whisper, msg!.Channel);
        Assert.Equal(111L, msg.SenderId);
        Assert.Equal(222L, msg.TargetId);
        Assert.Equal(0L, msg.PeerId);   // peer is decided by ChatService (needs the local charId), not the parser
    }

    [Fact]
    public void TryParseGetChipChatRecordsReply_Record_CarriesTargetId()
    {
        var reply = new WireBytes()
            .Tag(5, 2).LengthDelimited(ChitChatMsg(senderId: 111, targetId: 333))
            .ToArray();

        Assert.Equal(1, WireProtocol.TryParseGetChipChatRecordsReply(reply, out var messages));

        Assert.Equal(333L, messages[0].Msg.TargetId);
    }

    // ============ GetChipChatRecordsRequest.target_id ============

    [Fact]
    public void TryParseGetChipChatRecordsRequest_Private_ReadsChannelAndTarget()
    {
        var request = new WireBytes()
            .Tag(2, 0).Varint(5)                 // channel_type = ChannelPrivate
            .Tag(3, 0).Varint(0)                 // desc_msg_id
            .Tag(4, 0).Varint(30)                // record_num
            .Tag(5, 0).Varint(4_294_967_301UL)   // target_id (> uint32)
            .ToArray();
        var envelope = new WireBytes().Tag(1, 2).LengthDelimited(request).ToArray();

        Assert.True(WireProtocol.TryParseGetChipChatRecordsRequest(envelope, out var channel, out var target));

        Assert.Equal(5, channel);
        Assert.Equal(4_294_967_301L, target);
    }

    [Fact]
    public void TryParseGetChipChatRecordsRequest_World_TargetZero()
    {
        var request = new WireBytes().Tag(2, 0).Varint(1).Tag(4, 0).Varint(30).ToArray();
        var envelope = new WireBytes().Tag(1, 2).LengthDelimited(request).ToArray();

        Assert.True(WireProtocol.TryParseGetChipChatRecordsRequest(envelope, out var channel, out var target));

        Assert.Equal(1, channel);
        Assert.Equal(0L, target);
    }

    [Fact]
    public void TryParseGetChipChatRecordsRequest_BadEnvelope_False()
    {
        var notEnvelope = new WireBytes().Tag(2, 0).Varint(5).ToArray();

        Assert.False(WireProtocol.TryParseGetChipChatRecordsRequest(notEnvelope, out _, out _));
    }

    [Fact]
    public void TryParseGetChipChatRecordsRequest_NoChannel_False()
    {
        var request = new WireBytes().Tag(5, 0).Varint(42).ToArray();
        var envelope = new WireBytes().Tag(1, 2).LengthDelimited(request).ToArray();

        Assert.False(WireProtocol.TryParseGetChipChatRecordsRequest(envelope, out _, out var target));
        Assert.Equal(42L, target);
    }

    private static byte[] ChitChatMsg(ulong senderId, ulong targetId)
    {
        var sender = new WireBytes().Tag(1, 0).Varint(senderId).Tag(2, 2).String("Karu").ToArray();
        var info = new WireBytes()
            .Tag(1, 0).Varint(0)
            .Tag(2, 0).Varint(targetId)
            .Tag(3, 2).String("psst")
            .ToArray();
        return new WireBytes()
            .Tag(1, 0).Varint(7)
            .Tag(2, 2).LengthDelimited(sender)
            .Tag(3, 0).Varint(1_779_370_145)
            .Tag(4, 2).LengthDelimited(info)
            .ToArray();
    }
}
