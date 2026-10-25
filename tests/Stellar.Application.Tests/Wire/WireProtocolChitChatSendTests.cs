using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary><c>SendChitChatMsgReply</c> (cd_end_time / err_code) and <c>SendChitChatMsgRequest.channel_type</c>.</summary>
public sealed class WireProtocolChitChatSendTests
{
    [Fact]
    public void TryParseSendChitChatMsgReply_Accepted_ReadsCdEndAndShowStamp()
    {
        var show = new WireBytes()
            .Tag(1, 0).Varint(77)                 // msg_id
            .Tag(3, 0).Varint(1_779_370_145)      // timestamp (s)
            .ToArray();
        var reply = new WireBytes()
            .Tag(3, 2).LengthDelimited(show)
            .Tag(4, 0).Varint(1_779_370_150)      // cd_end_time (s) = +5 s
            .ToArray();
        var ret = new WireBytes().Tag(1, 2).LengthDelimited(reply).ToArray();   // SendChitChatMsg_Ret { ret = 1 }

        Assert.True(WireProtocol.TryParseSendChitChatMsgReply(WireProtocol.UnwrapReturnEnvelope(ret), out var r));

        Assert.Equal(0, r.ErrCode);
        Assert.Equal(1_779_370_150L, r.CdEndTime);
        Assert.Equal(1_779_370_145L, r.ShowMsgTimestamp);
        Assert.Equal(77L, r.ShowMsgId);
    }

    [Fact]
    public void TryParseSendChitChatMsgReply_TooFrequent_ReadsErrCode()
    {
        var reply = new WireBytes()
            .Tag(4, 0).Varint(1_779_370_150)
            .Tag(5, 0).Varint(5211)               // ErrChatSendCdNoEnd
            .ToArray();

        Assert.True(WireProtocol.TryParseSendChitChatMsgReply(reply, out var r));

        Assert.Equal(5211, r.ErrCode);
        Assert.Equal(1_779_370_150L, r.CdEndTime);
        Assert.Equal(0L, r.ShowMsgTimestamp);
    }

    [Fact]
    public void TryParseSendChitChatMsgReply_NoCooldown_IsZero()
    {
        var reply = new WireBytes().Tag(5, 0).Varint(0).ToArray();

        Assert.True(WireProtocol.TryParseSendChitChatMsgReply(reply, out var r));

        Assert.Equal(0L, r.CdEndTime);
        Assert.Equal(0, r.ErrCode);
    }

    [Fact]
    public void TryParseSendChitChatMsgReply_Truncated_False()
    {
        var bad = new byte[] { 0x20 };   // tag field 4 varint, value missing

        Assert.False(WireProtocol.TryParseSendChitChatMsgReply(bad, out _));
    }

    [Fact]
    public void ParseChannelTypeFromSendChitChatMsgRequest_ReadsField2()
    {
        var info = new WireBytes().Tag(1, 0).Varint(0).Tag(3, 2).String("hi").ToArray();
        var request = new WireBytes()
            .Tag(2, 0).Varint(2)                  // channel_type = ChannelScene ("Current")
            .Tag(4, 2).LengthDelimited(info)
            .ToArray();
        var envelope = new WireBytes().Tag(1, 2).LengthDelimited(request).ToArray();

        Assert.Equal(2, WireProtocol.ParseChannelTypeFromSendChitChatMsgRequest(envelope));
    }

    [Fact]
    public void ParseChannelTypeFromSendChitChatMsgRequest_NoEnvelope_MinusOne()
    {
        var raw = new WireBytes().Tag(2, 0).Varint(1).ToArray();

        Assert.Equal(-1, WireProtocol.ParseChannelTypeFromSendChitChatMsgRequest(raw));
    }
}
