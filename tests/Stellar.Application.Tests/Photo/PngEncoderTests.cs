using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Stellar.Application.Abstractions;
using Stellar.Application.Imaging;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class PngEncoderTests
{
    [Fact]
    public void Writes_signature_ihdr_and_flips_rows()
    {
        // 1x2 image, bottom-up: row0 (bottom) red, row1 (top) blue
        var rgba = new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 };
        var png = PngEncoder.Encode(new FrameGrab(rgba, 1, 2, null));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(1, BitConverter.ToInt32(Enumerable.Reverse(png[16..20]).ToArray()));
        Assert.Equal(2, BitConverter.ToInt32(Enumerable.Reverse(png[20..24]).ToArray()));
        var idatLen = BitConverter.ToInt32(Enumerable.Reverse(png[33..37]).ToArray());
        using var z = new ZLibStream(new MemoryStream(png, 41, idatLen), CompressionMode.Decompress);
        var raw = new MemoryStream(); z.CopyTo(raw);
        var r = raw.ToArray(); // [filter, rgba] per row, top row first
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 0, 255, 0, 0, 255 }, r);
    }
}
