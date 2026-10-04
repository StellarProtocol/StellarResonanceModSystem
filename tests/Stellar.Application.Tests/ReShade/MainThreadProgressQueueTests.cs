using System;
using Stellar.Infrastructure.Net;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

// Fix round 1 (I1): progress must only ever be delivered through this queue's Drain, never directly from
// whatever thread called Report — mirrors ResumeQueueTests' store-on-one-side / drain-on-the-other shape.
// Fix round 2 adds N1 (a throwing target must not stop delivery or escape Drain) and N2 (Finish).
public sealed class MainThreadProgressQueueTests
{
    private static MainThreadProgressQueue NewQueue() => new(new NullPluginLog());

    private sealed class CollectProgress : IProgress<double>
    {
        public readonly System.Collections.Generic.List<double> Values = new();
        public void Report(double value) => Values.Add(value);
    }

    private sealed class ThrowingProgress : IProgress<double>
    {
        public void Report(double value) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Report_never_calls_the_target_directly()
    {
        var q = NewQueue();
        var target = new CollectProgress();
        var slot = q.CreateSlot(target);

        q.Report(slot, 0.5);

        Assert.Empty(target.Values);
        Assert.Equal(1, q.Drain());   // it was queued for the main-thread drain
        Assert.Equal(new[] { 0.5 }, target.Values);
    }

    [Fact]
    public void Drain_delivers_the_latest_value_exactly_once()
    {
        var q = NewQueue();
        var target = new CollectProgress();
        var slot = q.CreateSlot(target);
        q.Report(slot, 0.1);
        q.Report(slot, 0.4);
        q.Report(slot, 0.9); // only the latest should survive to delivery

        var delivered = q.Drain();

        Assert.Equal(1, delivered);
        Assert.Equal(new[] { 0.9 }, target.Values);
        Assert.Equal(0, q.Drain());
    }

    [Fact]
    public void An_unchanged_value_is_not_redelivered()
    {
        var q = NewQueue();
        var target = new CollectProgress();
        var slot = q.CreateSlot(target);
        q.Report(slot, 0.5);
        q.Drain();

        q.Report(slot, 0.5); // same value again — must not re-mark dirty

        Assert.Equal(0, q.Drain());
        Assert.Equal(0, q.Drain());
        Assert.Equal(new[] { 0.5 }, target.Values); // delivered only the first time
    }

    [Fact]
    public void A_null_slot_is_a_no_op()
    {
        var q = NewQueue();
        q.Report(null, 0.5);
        Assert.Equal(0, q.Drain());
        Assert.Equal(0, q.Drain());
    }

    [Fact]
    public void Two_independent_slots_are_both_delivered()
    {
        var q = NewQueue();
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

    // ── Fix round 2 — N1: a throwing target must not stop delivery to the rest, nor escape Drain ──

    [Fact]
    public void A_throwing_target_does_not_stop_delivery_to_other_slots_or_escape_drain()
    {
        var q = NewQueue();
        var throwing = new ThrowingProgress();
        var ok = new CollectProgress();
        var slotA = q.CreateSlot(throwing);
        var slotB = q.CreateSlot(ok);
        q.Report(slotA, 0.3);
        q.Report(slotB, 0.6);

        var exception = Record.Exception(() => q.Drain());

        Assert.Null(exception);
        Assert.Equal(new[] { 0.6 }, ok.Values);
    }

    // ── Fix round 2 — N2: Finish flushes a slot exactly once when its download completes ──

    [Fact]
    public void Finish_with_a_value_stamps_it_as_the_one_to_deliver()
    {
        var q = NewQueue();
        var target = new CollectProgress();
        var slot = q.CreateSlot(target);
        q.Report(slot, 0.4); // a stale intermediate value, superseded below

        q.Finish(slot, 1.0);
        q.Drain();

        Assert.Equal(new[] { 1.0 }, target.Values);
    }

    [Fact]
    public void Finish_with_null_drops_the_pending_value_so_nothing_is_ever_delivered()
    {
        var q = NewQueue();
        var target = new CollectProgress();
        var slot = q.CreateSlot(target);
        q.Report(slot, 0.4);

        q.Finish(slot, null);

        Assert.Equal(0, q.Drain());
        Assert.Equal(0, q.Drain());
        Assert.Empty(target.Values);
    }

    [Fact]
    public void Finish_with_a_null_slot_is_a_no_op()
    {
        var q = NewQueue();
        q.Finish(null, 1.0);
        Assert.Equal(0, q.Drain());
    }
}
