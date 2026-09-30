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

    // Photo Studio fw fix round (perf review): the PNG streams straight to its output in fixed-size IDAT chunks
    // (running CRC) instead of buffering the whole compressed image. Every chunk must carry a valid CRC and the
    // concatenated IDAT payload must decode to the flipped rows.
    [Fact]
    public void Streams_multiple_idat_chunks_with_valid_crcs_that_decode_to_the_image()
    {
        const int w = 64, h = 48;
        var rng = new Random(7);
        var rgba = new byte[w * h * 4];
        rng.NextBytes(rgba);                                   // incompressible → many small chunks
        using var ms = new MemoryStream();
        PngEncoder.Encode(new FrameGrab(rgba, w, h, null), ms, idatChunkSize: 1024);
        var png = ms.ToArray();

        var idat = new MemoryStream();
        var idatChunks = 0;
        var pos = 8;
        string type;
        do
        {
            var len = ReadBe(png, pos);
            type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            Assert.Equal(Crc32(png, pos + 4, len + 4), (uint)ReadBe(png, pos + 8 + len));
            if (type == "IDAT") { idatChunks++; Assert.True(len <= 1024); idat.Write(png, pos + 8, len); }
            pos += 12 + len;
        } while (type != "IEND");
        Assert.Equal(png.Length, pos);
        Assert.True(idatChunks > 1, "expected the payload to span several IDAT chunks");

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream(); z.CopyTo(raw);
        var r = raw.ToArray();
        Assert.Equal(h * (1 + w * 4), r.Length);
        for (var row = 0; row < h; row++)
        {
            Assert.Equal(0, r[row * (1 + w * 4)]);            // filter byte
            var src = (h - 1 - row) * w * 4;                  // top row first = bottom-up source reversed
            Assert.True(r.AsSpan(row * (1 + w * 4) + 1, w * 4).SequenceEqual(rgba.AsSpan(src, w * 4)));
        }
    }

    [Fact]
    public void A_short_pixel_buffer_is_rejected_before_anything_is_written()
    {
        using var ms = new MemoryStream();
        Assert.ThrowsAny<Exception>(() => PngEncoder.Encode(new FrameGrab(new byte[3], 1, 1, null), ms));
        Assert.Equal(0, ms.Length);
    }

    private static int ReadBe(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    private static uint Crc32(byte[] data, int offset, int count)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = offset; i < offset + count; i++)
        {
            crc ^= data[i];
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
