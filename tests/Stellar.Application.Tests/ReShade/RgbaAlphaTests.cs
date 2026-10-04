using System;
using Stellar.Application.Imaging;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

// Probe fact (design § 11.1): after ReShade draws, the render target's alpha is 0 on every pixel — a PNG of it would be
// fully transparent. The capture forces every 4th byte (A of RGBA32) to 255 and leaves RGB untouched.
public sealed class RgbaAlphaTests
{
    [Fact]
    public void Every_alpha_byte_becomes_255_and_rgb_is_untouched()
    {
        var rgba = new byte[] { 10, 20, 30, 0, 40, 50, 60, 0, 70, 80, 90, 7 };
        RgbaAlpha.ForceOpaque(rgba);
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255, 70, 80, 90, 255 }, rgba);
    }

    [Fact]
    public void A_large_frame_is_fully_opaque()
    {
        var rgba = new byte[4 * 1031];   // odd pixel count exercises any vectorised tail
        for (var i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 7);
        var before = (byte[])rgba.Clone();
        RgbaAlpha.ForceOpaque(rgba);
        for (var i = 0; i < rgba.Length; i++)
            Assert.Equal(i % 4 == 3 ? (byte)255 : before[i], rgba[i]);
    }

    [Fact]
    public void Empty_and_null_buffers_are_ignored()
    {
        RgbaAlpha.ForceOpaque(Array.Empty<byte>());
        RgbaAlpha.ForceOpaque(null);
    }

    [Fact]
    public void A_buffer_that_is_not_whole_pixels_is_refused()
    {
        Assert.Throws<ArgumentException>(() => RgbaAlpha.ForceOpaque(new byte[6]));
    }
}
