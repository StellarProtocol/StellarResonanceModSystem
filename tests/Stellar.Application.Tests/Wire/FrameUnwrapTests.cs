using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Wire;

public sealed class FrameUnwrapTests
{
    [Fact]
    public void FrameDown_WrappingReturn_DispatchesInnerReturn()
    {
        var tap = new PandaWireTap(new StubLog());
        WireEnvelope? got = null;
        tap.RegisterReturn(e => got = e);

        var inner = FrameBytes.ReturnFrame(stubId: 5, callId: 99, errorId: 0, payload: new byte[] { 0x2A });
        var wrapped = FrameBytes.FrameDownWrapping(sequence: 1234, nestedFrames: inner);

        tap.HandleWireBytesForTest(wrapped);

        Assert.NotNull(got);
        Assert.Equal(WireMessageKind.Return, got!.Value.Kind);
        Assert.Equal(99u, got.Value.CallId);
        Assert.Equal(new byte[] { 0x2A }, got.Value.Payload.ToArray());
    }

    [Fact]
    public void FrameDown_ZstdNested_DispatchesInnerReturn()
    {
        var tap = new PandaWireTap(new StubLog());
        WireEnvelope? got = null;
        tap.RegisterReturn(e => got = e);

        var inner = FrameBytes.ReturnFrame(5, 7, 0, new byte[] { 1, 2, 3, 4 });
        var wrapped = FrameBytes.FrameDownWrapping(1, inner, zstd: true);

        tap.HandleWireBytesForTest(wrapped);

        Assert.NotNull(got);
        Assert.Equal(7u, got!.Value.CallId);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, got.Value.Payload.ToArray());
    }

    [Fact]
    public void FrameDown_WrappingNotify_DispatchesToServiceHandler()
    {
        var tap = new PandaWireTap(new StubLog());
        int hits = 0;
        tap.Register(serviceUuid: 966773353, methodId: 2, _ => hits++);

        var inner = FrameBytes.NotifyFrame(svc: 966773353, stub: 0, method: 2, payload: new byte[] { 0x10 });
        var wrapped = FrameBytes.FrameDownWrapping(9, inner);

        tap.HandleWireBytesForTest(wrapped);

        Assert.Equal(1, hits);
    }

    [Fact]
    public void NonWrappedReturn_StillDispatches()
    {
        var tap = new PandaWireTap(new StubLog());
        WireEnvelope? got = null;
        tap.RegisterReturn(e => got = e);

        tap.HandleWireBytesForTest(FrameBytes.ReturnFrame(1, 42, 0, new byte[] { 0xFF }));

        Assert.NotNull(got);
        Assert.Equal(42u, got!.Value.CallId);
    }
}
