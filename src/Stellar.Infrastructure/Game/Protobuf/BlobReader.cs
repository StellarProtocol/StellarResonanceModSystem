using System;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Minimal little-endian cursor over a byte buffer, used by
/// <see cref="ContainerDirtyDeltaReader"/> to walk the game's custom
/// container-delta format. Mirrors the BPSR-B <c>BlobReader</c>: signed
/// little-endian <c>i32</c> / <c>i64</c> reads and a bounds-checked skip.
///
/// <para>A mutable <c>struct</c> passed by <c>ref</c> so the offset advances
/// in place without heap allocation on the network-receive hot path. All reads
/// assume the caller has checked <see cref="Remaining"/> first; callers in the
/// reader guard every read so an out-of-range access cannot occur.</para>
/// </summary>
internal struct BlobReader
{
    // This game build writes a 4-byte 0xDEADBEEF canary AFTER every value in the
    // container-delta stream (i32 -> 4+4, i64 -> 8+4). We consume that guard after
    // each value. The skip is CONDITIONAL (only when the next word actually equals
    // the canary) so a guard-free stream — the unit tests, or another build that
    // omits the canary — still parses correctly; 0xDEADBEEF never occurs as a real
    // value in this data (sizes/slots/field-ids are small, uuids are read as i64).
    private const uint Guard = 0xDEADBEEF;

    private readonly byte[] _data;
    private int _offset;

    public BlobReader(byte[] data)
    {
        _data = data;
        _offset = 0;
    }

    public int Offset => _offset;

    public int Remaining => _data.Length - _offset;

    /// <summary>Reads a signed little-endian 32-bit int, advancing past the value
    /// and its trailing guard word (if present).</summary>
    public int ReadInt32()
    {
        var v = ReadRawUInt32();
        SkipGuard();
        return unchecked((int)v);
    }

    /// <summary>Reads a signed little-endian 64-bit int, advancing past the 8-byte
    /// value and its single trailing guard word (if present).</summary>
    public long ReadInt64()
    {
        var lo = (ulong)ReadRawUInt32();
        var hi = (ulong)ReadRawUInt32();
        SkipGuard();
        return unchecked((long)(lo | (hi << 32)));
    }

    // Reads 4 raw little-endian bytes WITHOUT consuming a guard.
    private uint ReadRawUInt32()
    {
        var v = (uint)_data[_offset]
            | ((uint)_data[_offset + 1] << 8)
            | ((uint)_data[_offset + 2] << 16)
            | ((uint)_data[_offset + 3] << 24);
        _offset += 4;
        return v;
    }

    // Consume the 4-byte canary that follows each value in this build, but only if
    // it is actually present (so guard-free streams are unaffected).
    private void SkipGuard()
    {
        if (Remaining < 4) return;
        var g = (uint)_data[_offset]
            | ((uint)_data[_offset + 1] << 8)
            | ((uint)_data[_offset + 2] << 16)
            | ((uint)_data[_offset + 3] << 24);
        if (g == Guard) _offset += 4;
    }

    /// <summary>Advances the cursor by <paramref name="count"/> bytes.</summary>
    public void Skip(int count) => _offset += count;

    /// <summary>
    /// Returns up to <paramref name="count"/> little-endian i32 values starting
    /// at the current offset WITHOUT advancing — for diagnostics that want to
    /// show the raw structure around a parse position.
    /// </summary>
    public int[] PeekInt32s(int count)
    {
        var n = Math.Min(count, Remaining / 4);
        if (n <= 0) return Array.Empty<int>();
        var result = new int[n];
        var at = _offset;
        for (var i = 0; i < n; i++)
        {
            result[i] = (int)((uint)_data[at]
                | ((uint)_data[at + 1] << 8)
                | ((uint)_data[at + 2] << 16)
                | ((uint)_data[at + 3] << 24));
            at += 4;
        }
        return result;
    }
}
