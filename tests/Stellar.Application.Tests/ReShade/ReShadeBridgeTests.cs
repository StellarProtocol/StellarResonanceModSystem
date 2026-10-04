using System;
using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class ReShadeBridgeTests
{
    // On the test host the add-on (and kernel32 itself, off Windows) is absent: every call returns its default.
    [Fact]
    public void Without_the_add_on_every_call_returns_a_default_and_nothing_throws()
    {
        var bridge = new ReShadeBridge(new StubLog());
        Assert.False(bridge.IsLoaded);
        Assert.Equal(default, bridge.ReadStatus());
        Assert.False(bridge.TryGetTechnique(0, out _, out _, out _));
        Assert.Null(bridge.GetPreset());
        bridge.RequestEnabled(true);
        bridge.RequestTechnique(null, "Clarity", true, true);
        bridge.RequestPreset("p.ini");
        bridge.RequestSearchPaths("a", null);
        bridge.QueueRender(IntPtr.Zero, 1, 1);
        Assert.Equal(-1, bridge.LastRender());
        Assert.Equal(IntPtr.Zero, bridge.RenderEventFunc());
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0x42, 0, 0x43 }, "AB")]
    [InlineData(new byte[] { 0x41, 0x42 }, "AB")]
    [InlineData(new byte[] { 0 }, "")]
    [InlineData(new byte[] { 0xC3, 0xA9, 0 }, "\u00e9")]
    public void Buffers_decode_as_UTF8_up_to_the_first_NUL(byte[] buffer, string expected)
    {
        Assert.Equal(expected, ReShadeBridge.DecodeZ(buffer));
    }

    [Fact]
    public void Strings_encode_as_NUL_terminated_UTF8()
    {
        Assert.Equal(new byte[] { 0x41, 0xC3, 0xA9, 0 }, ReShadeBridge.EncodeZ("A\u00e9"));
        Assert.Null(ReShadeBridge.EncodeZ(null));
        Assert.Null(ReShadeBridge.EncodeZ(""));
    }
}
