using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>Drain finishes rich content (segments, sanitised voice) and the position-share API gates.</summary>
public sealed class ChatServiceHypertextTests
{
    private static ChatMessage Msg(ChatMessageType type) => new(
        Channel: ChatChannel.World, SenderName: "S", SenderId: 1, Text: string.Empty, Timestamp: DateTime.UnixEpoch,
        Type: type, RawPayload: ReadOnlyMemory<byte>.Empty, Sender: null, MsgId: 1);

    private static (ChatService Svc, List<ChatMessage> Got, StubProbe Probe) Service()
    {
        var svc = new ChatService(new StubLog());
        var probe = new StubProbe();
        svc.AttachProbe(probe);
        var got = new List<ChatMessage>();
        svc.MessageReceived += got.Add;
        return (svc, got, probe);
    }

    [Fact]
    public void Drain_BuildsSegments_AndSanitisesVoice()
    {
        var (svc, got, probe) = Service();
        var pos = new ChatPlaceholder
        {
            Type = ChatPlaceholderType.ScenePosition, Position = new ChatPosition { SceneId = 9, LineId = 1, X = 1, Y = 2, Z = 3 },
        };
        probe.OnMessageReceived!(Msg(ChatMessageType.ItemLink) with
            { Hypertext = new ChatHypertext { ConfigId = 3000002, Placeholders = new[] { pos } } });
        probe.OnMessageReceived!(Msg(ChatMessageType.Unknown) with
            { Voice = new ChatVoice { Seconds = 4, Transcript = "<size=200>hi</size>" } });
        probe.OnMessageReceived!(Msg(ChatMessageType.Regular));

        svc.Drain();

        Assert.Equal(3, got.Count);
        Assert.Equal(ChatLinkKind.Position, got[0].Hypertext!.Kind);
        Assert.Contains(got[0].Hypertext!.Segments, s => s.Kind == ChatLinkKind.Position && s.Position!.SceneId == 9);
        Assert.Equal("hi", got[1].Voice!.Transcript);
        Assert.Null(got[2].Hypertext);
        Assert.Null(got[2].Voice);
    }

    [Fact]
    public void ShareApi_WithoutLua_IsNotReady_AndOffThread_IsWrongThread()
    {
        var (svc, _, _) = Service();
        svc.Drain();   // learns the "main" thread

        Assert.Equal(ChatShareStatus.NotReady, svc.SharePosition(ChatTarget.World, "x").Status);
        Assert.Equal(ChatShareStatus.NotReady, svc.TrackPosition(new ChatPosition { SceneId = 9 }).Status);
        Assert.False(svc.GetSharePosition().Available);
        Assert.Equal(ChatShareStatus.Unsupported, svc.TrackPosition(new ChatPosition()).Status);

        var off = ChatShareStatus.Ok;
        var t = new Thread(() => off = svc.TrackPosition(new ChatPosition { SceneId = 9 }).Status);   // a real other thread
        t.Start();
        t.Join();
        Assert.Equal(ChatShareStatus.WrongThread, off);
    }
}
