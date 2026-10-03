using System;
using Stellar.Infrastructure.Net;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

// Fix round 1 (I1): progress must only ever be delivered through this queue's Drain, never directly from
// whatever thread called Report — mirrors ResumeQueueTests' store-on-one-side / drain-on-the-other shape.
public sealed class MainThreadProgressQueueTests
{
    private sealed class CollectProgress : IProgress<double>
    {
        public readonly System.Collections.Generic.List<double> Values = new();
        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public void Report_never_calls_the_target_directly()
    {
        var q = new MainThreadProgressQueue();
        var target = new CollectProgress();
        var slot = q.CreateSlot(target);

        q.Report(slot, 0.5);

        Assert.Empty(target.Values);
        Assert.True(q.HasQueued);
    }

    [Fact]
    public void Drain_delivers_the_latest_value_exactly_once()
    {
        var q = new MainThreadProgressQueue();
        var target = new CollectProgress();
        var slot = q.CreateSlot(target);
        q.Report(slot, 0.1);
        q.Report(slot, 0.4);
        q.Report(slot, 0.9); // only the latest should survive to delivery

        var delivered = q.Drain();

        Assert.Equal(1, delivered);
        Assert.Equal(new[] { 0.9 }, target.Values);
        Assert.False(q.HasQueued);
    }

    [Fact]
    public void An_unchanged_value_is_not_redelivered()
    {
        var q = new MainThreadProgressQueue();
        var target = new CollectProgress();
        var slot = q.CreateSlot(target);
        q.Report(slot, 0.5);
        q.Drain();

        q.Report(slot, 0.5); // same value again — must not re-mark dirty

        Assert.False(q.HasQueued);
        Assert.Equal(0, q.Drain());
        Assert.Equal(new[] { 0.5 }, target.Values); // delivered only the first time
    }

    [Fact]
    public void A_null_slot_is_a_no_op()
    {
        var q = new MainThreadProgressQueue();
        q.Report(null, 0.5);
        Assert.False(q.HasQueued);
        Assert.Equal(0, q.Drain());
    }

    [Fact]
    public void Two_independent_slots_are_both_delivered()
    {
        var q = new MainThreadProgressQueue();
        var a = new CollectProgress();
        var b = new CollectProgress();
        var slotA = q.CreateSlot(a);
        var slotB = q.CreateSlot(b);
        q.Report(slotA, 0.2);
        q.Report(slotB, 0.7);

        var delivered = q.Drain();

        Assert.Equal(2, delivered);
        Assert.Equal(new[] { 0.2 }, a.Values);
        Assert.Equal(new[] { 0.7 }, b.Values);
    }
}
