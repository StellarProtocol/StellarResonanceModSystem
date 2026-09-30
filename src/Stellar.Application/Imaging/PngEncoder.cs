using System;
using System.IO;
using System.IO.Compression;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Imaging;

/// <summary>
/// Minimal RGBA8 PNG writer (filter 0). Thread-safe, pure BCL. Streams: the zlib output goes straight to the
/// destination as fixed-size IDAT chunks, each with a running CRC, so a 4× capture never holds the whole
/// compressed image in memory (Photo Studio fw fix round, perf review).
/// </summary>
internal static class PngEncoder
{
    /// <summary>IDAT payload per chunk. Any size is valid PNG; 64 KiB keeps the buffer small and the chunk count low.</summary>
    public const int DefaultIdatChunkSize = 64 * 1024;

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly byte[] FilterNone = { 0 };

    /// <summary>Encodes into a byte array (small images / tests). Production streams via <see cref="Encode(FrameGrab, Stream, int)"/>.</summary>
    public static byte[] Encode(FrameGrab g)
    {
        using var ms = new MemoryStream();
        Encode(g, ms);
        return ms.ToArray();
    }

    /// <summary>Throws before any byte is written when <paramref name="g"/> cannot be encoded.</summary>
    public static void EnsureEncodable(FrameGrab g)
    {
        if (g.Width <= 0 || g.Height <= 0) throw new InvalidDataException($"Invalid frame size {g.Width}x{g.Height}.");
        if (g.RgbaBottomUp.LongLength < (long)g.Width * g.Height * 4)
            throw new InvalidDataException($"The frame buffer holds {g.RgbaBottomUp.Length} bytes, expected {(long)g.Width * g.Height * 4}.");
    }

    /// <summary>Writes the PNG to <paramref name="output"/> (left open).</summary>
    public static void Encode(FrameGrab g, Stream output, int idatChunkSize = DefaultIdatChunkSize)
    {
        EnsureEncodable(g);
        output.Write(Signature);
        var ihdr = new byte[13];
        WriteBe(ihdr, 0, g.Width);
        WriteBe(ihdr, 4, g.Height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        WriteChunk(output, "IHDR", ihdr, ihdr.Length);
        using (var idat = new IdatChunkStream(output, idatChunkSize))
        {
            using var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true);
            var stride = g.Width * 4;
            for (var y = g.Height - 1; y >= 0; y--)
            {
                z.Write(FilterNone, 0, 1);
                z.Write(g.RgbaBottomUp, y * stride, stride);
            }
        } // zlib flushes its trailer into idat, then idat emits its last partial chunk
        WriteChunk(output, "IEND", Array.Empty<byte>(), 0);
    }

    private static void WriteChunk(Stream s, string type, byte[] data, int length)
    {
        var header = new byte[8];
        WriteBe(header, 0, length);
        for (var i = 0; i < 4; i++) header[4 + i] = (byte)type[i];
        s.Write(header, 0, 8);
        s.Write(data, 0, length);
        var crc = Crc(Crc(0xFFFFFFFFu, header, 4, 4), data, 0, length) ^ 0xFFFFFFFFu;
        var c = new byte[4];
        WriteBe(c, 0, (int)crc);
        s.Write(c, 0, 4);
    }

    private static uint Crc(uint crc, byte[] data, int offset, int count)
    {
        for (var i = offset; i < offset + count; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
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

    /// <summary>
    /// Write-only sink the zlib stream compresses into: collects up to <c>chunkSize</c> bytes (CRC updated as they
    /// arrive), then emits one IDAT chunk. Dispose emits the final partial chunk.
    /// </summary>
    private sealed class IdatChunkStream : Stream
    {
        private static readonly byte[] IdatType = { (byte)'I', (byte)'D', (byte)'A', (byte)'T' };
        private readonly Stream _out;
        private readonly byte[] _buf;
        private readonly byte[] _scratch = new byte[8];
        private int _len;
        private uint _crc;
        private bool _disposed;

        public IdatChunkStream(Stream output, int chunkSize)
        {
            if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            _out = output;
            _buf = new byte[chunkSize];
            _crc = Crc(0xFFFFFFFFu, IdatType, 0, 4);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var n = Math.Min(buffer.Length, _buf.Length - _len);
                buffer[..n].CopyTo(_buf.AsSpan(_len));
                for (var i = 0; i < n; i++) _crc = CrcTable[(_crc ^ _buf[_len + i]) & 0xFF] ^ (_crc >> 8);
                _len += n;
                buffer = buffer[n..];
                if (_len == _buf.Length) EmitChunk();
            }
        }

        private void EmitChunk()
        {
            if (_len == 0) return;
            WriteBe(_scratch, 0, _len);
            IdatType.CopyTo(_scratch, 4);
            _out.Write(_scratch, 0, 8);
            _out.Write(_buf, 0, _len);
            WriteBe(_scratch, 0, (int)(_crc ^ 0xFFFFFFFFu));
            _out.Write(_scratch, 0, 4);
            _len = 0;
            _crc = Crc(0xFFFFFFFFu, IdatType, 0, 4);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                EmitChunk();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }   // chunk boundaries are ours; the final chunk is emitted on Dispose
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
