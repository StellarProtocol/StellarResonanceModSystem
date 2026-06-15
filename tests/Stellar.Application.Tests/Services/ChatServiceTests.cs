using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public sealed class ChatServiceTests
{
    // -------- Drain semantics --------

    [Fact]
    public void Drain_FlushesEveryEnqueuedMessage_ToMessageReceived()
    {
        var (svc, probe) = NewService();
        var received = new List<ChatMessage>();
        svc.MessageReceived += received.Add;

        probe.OnMessageReceived!(MakeMsg(1, "alice"));
        probe.OnMessageReceived!(MakeMsg(2, "bob"));
        probe.OnMessageReceived!(MakeMsg(3, "carol"));
        svc.Drain();

        Assert.Equal(3, received.Count);
        Assert.Collection(received,
            m => Assert.Equal(1, m.MsgId),
            m => Assert.Equal(2, m.MsgId),
            m => Assert.Equal(3, m.MsgId));
    }

    [Fact]
    public void Drain_OnEmptyQueue_DoesNothing()
    {
        var (svc, _) = NewService();
        var received = new List<ChatMessage>();
        svc.MessageReceived += received.Add;

        svc.Drain();

        Assert.Empty(received);
    }

    // -------- Ring + RecentMessages --------

    [Fact]
    public void RecentMessages_BeforeDrain_IsEmpty()
    {
        var (svc, probe) = NewService();
        probe.OnMessageReceived!(MakeMsg(1));

        Assert.Empty(svc.RecentMessages);
    }

    [Fact]
    public void RecentMessages_AfterDrain_ReturnsAllInOrder()
    {
        var (svc, probe) = NewService();
        probe.OnMessageReceived!(MakeMsg(1));
        probe.OnMessageReceived!(MakeMsg(2));
        probe.OnMessageReceived!(MakeMsg(3));
        svc.Drain();

        var ids = svc.RecentMessages.Select(m => m.MsgId).ToArray();
        Assert.Equal(new long[] { 1, 2, 3 }, ids);
    }

    [Fact]
    public void Ring_EvictsOldest_WhenAtCapacity()
    {
        // RingCapacity is internal const (500). Enqueue capacity+10 and verify
        // the oldest 10 fell out and the newest 500 are retained in order.
        var (svc, probe) = NewService();
        const int cap = 500;
        const int extra = 10;
        for (var i = 1; i <= cap + extra; i++) probe.OnMessageReceived!(MakeMsg(i));
        svc.Drain();

        var ids = svc.RecentMessages.Select(m => m.MsgId).ToArray();
        Assert.Equal(cap, ids.Length);
        Assert.Equal(extra + 1L, ids[0]);              // oldest retained is msg #11
        Assert.Equal((long)(cap + extra), ids[^1]);    // newest retained is msg #510
    }

    [Fact]
    public void RecentMessages_ReusesSnapshot_WhenRingHasNotChanged()
    {
        var (svc, probe) = NewService();
        probe.OnMessageReceived!(MakeMsg(1));
        svc.Drain();

        var first = svc.RecentMessages;
        var second = svc.RecentMessages;

        Assert.Same(first, second); // versioned snapshot must NOT reallocate per read
    }

    [Fact]
    public void RecentMessages_RebuildsSnapshot_WhenRingChanges()
    {
        var (svc, probe) = NewService();
        probe.OnMessageReceived!(MakeMsg(1));
        svc.Drain();
        var first = svc.RecentMessages;

        probe.OnMessageReceived!(MakeMsg(2));
        svc.Drain();
        var second = svc.RecentMessages;

        Assert.NotSame(first, second);
        Assert.Equal(2, second.Count);
    }

    // -------- MessageReceived subscription lifecycle --------

    [Fact]
    public void MessageReceived_MultipleSubscribers_AllFire()
    {
        var (svc, probe) = NewService();
        var a = new List<long>();
        var b = new List<long>();
        svc.MessageReceived += m => a.Add(m.MsgId);
        svc.MessageReceived += m => b.Add(m.MsgId);

        probe.OnMessageReceived!(MakeMsg(42));
        svc.Drain();

        Assert.Equal(new long[] { 42 }, a);
        Assert.Equal(new long[] { 42 }, b);
    }

    [Fact]
    public void MessageReceived_Unsubscribe_StopsReceiving()
    {
        var (svc, probe) = NewService();
        var hits = 0;
        Action<ChatMessage> handler = _ => hits++;
        svc.MessageReceived += handler;
        probe.OnMessageReceived!(MakeMsg(1));
        svc.Drain();
        Assert.Equal(1, hits);

        svc.MessageReceived -= handler;
        probe.OnMessageReceived!(MakeMsg(2));
        svc.Drain();
        Assert.Equal(1, hits); // unchanged
    }

    [Fact]
    public void MessageReceived_SubscriberThrows_DoesNotPropagate_AndPeersStillFire()
    {
        var (svc, probe) = NewService();
        var peerHits = 0;
        svc.MessageReceived += _ => throw new InvalidOperationException("boom");
        svc.MessageReceived += _ => peerHits++;

        probe.OnMessageReceived!(MakeMsg(1));
        var ex = Record.Exception(() => svc.Drain());

        Assert.Null(ex);            // Drain swallows subscriber failures
        Assert.Equal(1, peerHits);  // peer must still fire
    }

    // -------- Send delegation --------

    [Fact]
    public void Send_BeforeAttachProbe_DoesNotThrow()
    {
        var log = new StubLog();
        var svc = new ChatService(log);

        var ex = Record.Exception(() => svc.Send(ChatTarget.World, "hi"));

        Assert.Null(ex);
        Assert.Contains(log.InfoLines, l => l.Contains("probe not attached"));
    }

    [Fact]
    public void Send_DelegatesToProbeTrySend()
    {
        var (svc, probe) = NewService();
        probe.NextTrySendResult = true;

        svc.Send(ChatTarget.Party, "hello");

        Assert.Single(probe.SendCalls);
        Assert.Equal(ChatTarget.Party, probe.SendCalls[0].Target);
        Assert.Equal("hello", probe.SendCalls[0].Text);
    }

    [Fact]
    public void Send_TrySendReturnsFalse_LogsReason()
    {
        var log = new StubLog();
        var svc = new ChatService(log);
        var probe = new StubProbe { NextTrySendResult = false, NextFailureReason = "rate limited" };
        svc.AttachProbe(probe);

        svc.Send(ChatTarget.World, "spam");

        Assert.Contains(log.InfoLines, l => l.Contains("rate limited"));
    }

    [Fact]
    public void Send_TrySendThrows_LogsWarning_DoesNotPropagate()
    {
        var log = new StubLog();
        var svc = new ChatService(log);
        var probe = new StubProbe { TrySendShouldThrow = true };
        svc.AttachProbe(probe);

        var ex = Record.Exception(() => svc.Send(ChatTarget.Say, "boom"));

        Assert.Null(ex);
        Assert.Contains(log.WarningLines, l => l.Contains("send threw"));
    }

    // -------- Helpers --------

    private static (ChatService svc, StubProbe probe) NewService()
    {
        var svc = new ChatService(new StubLog());
        var probe = new StubProbe();
        svc.AttachProbe(probe);
        return (svc, probe);
    }

    private static ChatMessage MakeMsg(long msgId, string sender = "alice") => new(
        Channel:    ChatChannel.World,
        SenderName: sender,
        SenderId:   msgId * 10,
        Text:       "msg-" + msgId,
        Timestamp:  DateTime.UnixEpoch.AddSeconds(msgId),
        Type:       ChatMessageType.Regular,
        RawPayload: ReadOnlyMemory<byte>.Empty,
        Sender:     null,
        MsgId:      msgId);
}
