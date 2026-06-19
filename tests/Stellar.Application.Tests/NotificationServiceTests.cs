using System.Linq;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

public sealed class NotificationServiceTests
{
    // Controllable monotonic clock so expiry is deterministic without the game.
    private sealed class Clock
    {
        public double Now;
    }

    private static (NotificationService svc, Clock clock) Build()
    {
        var clock = new Clock();
        return (new NotificationService(() => clock.Now), clock);
    }

    [Fact]
    public void Notify_then_Drain_returns_active_toast()
    {
        var (svc, clock) = Build();
        svc.Notify("hello", NotificationKind.Info, 3f);

        var active = svc.Drain(clock.Now);

        Assert.Single(active);
        Assert.Equal("hello", active[0].Message);
        Assert.Equal(NotificationKind.Info, active[0].Kind);
    }

    [Fact]
    public void Drain_drops_expired_toasts()
    {
        var (svc, clock) = Build();
        svc.Notify("short", NotificationKind.Warning, 2f);   // expires at t=2

        Assert.Single(svc.Drain(1.9));   // still alive just before expiry
        Assert.Empty(svc.Drain(2.0));    // ExpiresAt <= now -> dropped
    }

    [Fact]
    public void Default_lifetime_used_when_seconds_null()
    {
        var (svc, clock) = Build();
        svc.Notify("def");   // ~DefaultSeconds lifetime

        Assert.Single(svc.Drain(NotificationService.DefaultSeconds - 0.01));
        Assert.Empty(svc.Drain(NotificationService.DefaultSeconds));
    }

    [Fact]
    public void Drain_preserves_insertion_order_oldest_first()
    {
        var (svc, clock) = Build();
        svc.Notify("first", NotificationKind.Info, 10f);
        clock.Now = 1.0;
        svc.Notify("second", NotificationKind.Success, 10f);
        clock.Now = 2.0;
        svc.Notify("third", NotificationKind.Error, 10f);

        var active = svc.Drain(clock.Now);

        Assert.Equal(new[] { "first", "second", "third" }, active.Select(t => t.Message).ToArray());
    }

    [Fact]
    public void Drain_keeps_unexpired_after_dropping_expired_in_order()
    {
        var (svc, clock) = Build();
        svc.Notify("gone", NotificationKind.Info, 1f);    // expires at t=1
        svc.Notify("stays", NotificationKind.Info, 5f);   // expires at t=5

        var active = svc.Drain(2.0);

        Assert.Single(active);
        Assert.Equal("stays", active[0].Message);
    }

    [Fact]
    public void Empty_or_nonpositive_messages_are_ignored()
    {
        var (svc, clock) = Build();
        svc.Notify("", NotificationKind.Info, 3f);
        svc.Notify("x", NotificationKind.Info, 0f);     // non-positive lifetime
        svc.Notify("y", NotificationKind.Info, -1f);

        Assert.Empty(svc.Drain(clock.Now));
    }
}
