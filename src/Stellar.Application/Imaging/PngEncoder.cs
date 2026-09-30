using System;
using System.IO;
using System.IO.Compression;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Imaging;

/// <summary>Minimal RGBA8 PNG writer (filter 0). Thread-safe, pure BCL.</summary>
internal static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(FrameGrab g)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, g.Width);
        WriteBe(ihdr, 4, g.Height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", Deflate(g));
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static byte[] Deflate(FrameGrab g)
    {
        var stride = g.Width * 4;
        using var outMs = new MemoryStream();
        using (var z = new ZLibStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = g.Height - 1; y >= 0; y--)
            {
                z.WriteByte(0);
                z.Write(g.RgbaBottomUp, y * stride, stride);
            }
        }
        return outMs.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe(len, 0, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        var c = new byte[4];
        WriteBe(c, 0, (int)crc);
        s.Write(c);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static void WriteBe(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
}
