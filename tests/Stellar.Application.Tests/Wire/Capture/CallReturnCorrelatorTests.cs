using Stellar.Infrastructure.Game.Capture;
using Xunit;

namespace Stellar.Application.Tests.Wire.Capture;

public sealed class CallReturnCorrelatorTests
{
    [Fact]
    public void Resolve_AfterNoteCall_ReturnsSvcMethod()
    {
        var c = new CallReturnCorrelator(capacity: 4);
        c.NoteCall(callId: 10, serviceUuid: 103198054, methodId: 311327);

        Assert.True(c.Resolve(10, out var svc, out var method));
        Assert.Equal(103198054ul, svc);
        Assert.Equal(311327u, method);
    }

    [Fact]
    public void Resolve_Unknown_False()
    {
        var c = new CallReturnCorrelator(4);
        Assert.False(c.Resolve(999, out _, out _));
    }

    [Fact]
    public void Capacity_EvictsOldest_AndCounts()
    {
        var c = new CallReturnCorrelator(capacity: 2);
        c.NoteCall(1, 100, 1);
        c.NoteCall(2, 100, 2);
        c.NoteCall(3, 100, 3);

        Assert.False(c.Resolve(1, out _, out _));
        Assert.True(c.Resolve(3, out _, out _));
        Assert.Equal(1, c.EvictionCount);
    }
}
