using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>Server-driven send cooldown + <c>SendResult</c> event in <see cref="ChatService"/>.</summary>
public sealed class ChatServiceSendResultTests
{
    private const long Now = 1_779_370_145_000;   // server ms

    [Fact]
    public void Reply_WithCooldown_ReportsRemaining_AgainstServerClock()
    {
        var (svc, probe, _) = New(() => Now);

        probe.OnSendResult!(Result(ChatChannel.World, ok: true, err: 0, endSec: 1_779_370_150));

        Assert.True(svc.TryGetSendCooldown(ChatChannel.World, out var rem));
        Assert.Equal(5.0, rem, 3);
        Assert.False(svc.TryGetSendCooldown(ChatChannel.Party, out var other));
        Assert.Equal(0, other);
    }

    [Fact]
    public void Cooldown_Expired_ReturnsFalse()
    {
        long now = Now;
        var (svc, probe, _) = New(() => now);
        probe.OnSendResult!(Result(ChatChannel.World, ok: true, err: 0, endSec: 1_779_370_150));

        now = Now + 6_000;

        Assert.False(svc.TryGetSendCooldown(ChatChannel.World, out var rem));
        Assert.Equal(0, rem);
    }

    [Fact]
    public void AcceptedWithoutCooldown_ClearsSlot()
    {
        var (svc, probe, _) = New(() => Now);
        probe.OnSendResult!(Result(ChatChannel.Guild, ok: true, err: 0, endSec: 1_779_370_150));

        probe.OnSendResult!(Result(ChatChannel.Guild, ok: true, err: 0, endSec: 0));

        Assert.False(svc.TryGetSendCooldown(ChatChannel.Guild, out _));
    }

    [Fact]
    public void ServerClockUnsynced_FallsBackToReplyStamp()
    {
        var (svc, probe, _) = New(() => 0);

        probe.OnSendResult!(Result(ChatChannel.World, ok: true, err: 0, endSec: 1_779_370_150)
            with { ServerTimeUtc = DateTimeOffset.FromUnixTimeSeconds(1_779_370_145).UtcDateTime });

        Assert.True(svc.TryGetSendCooldown(ChatChannel.World, out var rem));
        Assert.InRange(rem, 4.5, 5.0);
    }

    [Fact]
    public void SendResult_FiresOnDrain_NotBefore()
    {
        var (svc, probe, got) = New(() => Now);

        probe.OnSendResult!(Result(ChatChannel.World, ok: false, err: ChatSendResult.ErrorTooFrequent, endSec: 1_779_370_150));
        Assert.Empty(got);
        svc.Drain();

        var r = Assert.Single(got);
        Assert.False(r.Ok);
        Assert.Equal(5211, r.ErrorCode);
        Assert.True(svc.TryGetSendCooldown(ChatChannel.World, out _));   // refused-with-cd still updates the slot
    }

    private static ChatSendResult Result(ChatChannel ch, bool ok, int err, long endSec) => new(
        ch, ok, err, endSec > 0 ? DateTimeOffset.FromUnixTimeSeconds(endSec).UtcDateTime : null);

    private static (ChatService, StubProbe, List<ChatSendResult>) New(Func<long> serverNowMs)
    {
        var svc = new ChatService(new StubLog()) { ServerNowMs = serverNowMs };
        var probe = new StubProbe();
        svc.AttachProbe(probe);
        var got = new List<ChatSendResult>();
        svc.SendResult += got.Add;
        return (svc, probe, got);
    }
}
