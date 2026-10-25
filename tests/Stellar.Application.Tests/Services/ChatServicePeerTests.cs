using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>Whisper <see cref="ChatMessage.PeerId"/> resolution in <see cref="ChatService.Drain"/>.</summary>
public sealed class ChatServicePeerTests
{
    private const long Self = 1000;

    [Fact]
    public void OwnEcho_PeerIsTarget()
    {
        var m = DrainOne(Self, Whisper(sender: Self, target: 2000));
        Assert.Equal(2000L, m.PeerId);
    }

    [Fact]
    public void Incoming_PeerIsSender()
    {
        var m = DrainOne(Self, Whisper(sender: 3000, target: Self));
        Assert.Equal(3000L, m.PeerId);
    }

    [Fact]
    public void NoTarget_PeerIsSender_EvenWhenSelfUnknown()
    {
        var m = DrainOne(0, Whisper(sender: 3000, target: 0));
        Assert.Equal(3000L, m.PeerId);
    }

    [Fact]
    public void SelfUnknown_WithTarget_Undecided()
    {
        var m = DrainOne(0, Whisper(sender: 3000, target: 2000));
        Assert.Equal(0L, m.PeerId);
    }

    [Fact]
    public void PreTaggedPeer_IsKept()
    {
        var m = DrainOne(Self, Whisper(sender: Self, target: 2000) with { PeerId = 4000 });
        Assert.Equal(4000L, m.PeerId);
    }

    [Fact]
    public void NonWhisper_Untouched()
    {
        var m = DrainOne(Self, Whisper(sender: 3000, target: 0) with { Channel = ChatChannel.World });
        Assert.Equal(0L, m.PeerId);
    }

    [Fact]
    public void Reply_ToOwnEcho_TargetsPeer()
    {
        var m = DrainOne(Self, Whisper(sender: Self, target: 2000));
        var target = Assert.IsType<ChatTarget.WhisperTarget>(ChatTarget.Reply(m));
        Assert.Equal(2000L, target.TargetId);
    }

    private static ChatMessage DrainOne(long self, ChatMessage input)
    {
        var svc = new ChatService(new StubLog()) { SelfCharId = () => self };
        var probe = new StubProbe();
        svc.AttachProbe(probe);
        var got = new List<ChatMessage>();
        svc.MessageReceived += got.Add;
        probe.OnMessageReceived!(input);
        svc.Drain();
        return Assert.Single(got);
    }

    private static ChatMessage Whisper(long sender, long target) => new(
        Channel:    ChatChannel.Whisper,
        SenderName: "x",
        SenderId:   sender,
        Text:       "psst",
        Timestamp:  DateTime.UnixEpoch,
        Type:       ChatMessageType.Regular,
        RawPayload: ReadOnlyMemory<byte>.Empty,
        Sender:     null,
        MsgId:      1)
    { TargetId = target };
}
