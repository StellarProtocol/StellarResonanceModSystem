using System.IO;

namespace Stellar.Application.Tests.Inventory;

/// <summary>
/// Little-endian builder for the game's custom container-delta format, used to
/// drive <c>ContainerDirtyDeltaReader</c> in tests with bytes produced by an
/// INDEPENDENT encoder (no risk of testing the parser against the same code
/// that produced the input). Mirrors the BPSR-B BlobReader layout: signed
/// little-endian i32/i64, BEGIN(-2)/END(-3)/skip(-4)/add-only(-1) sentinels.
/// </summary>
internal sealed class DeltaBytes
{
    private const int TagBegin = -2;
    private const int TagEnd = -3;
    private const uint Guard = 0xDEADBEEF;

    private readonly MemoryStream _ms = new();
    private readonly bool _guards;

    /// <param name="guards">When true, emit a 4-byte 0xDEADBEEF canary after each
    /// value — replicating the SEA build's dirty-delta wire so the reader's
    /// guard-skip path is exercised. Default false = tight (global-build) layout.</param>
    public DeltaBytes(bool guards = false) => _guards = guards;

    public byte[] ToArray() => _ms.ToArray();

    private void WriteRaw32(int value)
    {
        _ms.WriteByte((byte)(value & 0xFF));
        _ms.WriteByte((byte)((value >> 8) & 0xFF));
        _ms.WriteByte((byte)((value >> 16) & 0xFF));
        _ms.WriteByte((byte)((value >> 24) & 0xFF));
    }

    private void MaybeGuard()
    {
        if (_guards) WriteRaw32(unchecked((int)Guard));
    }

    public DeltaBytes Int32(int value)
    {
        WriteRaw32(value);
        MaybeGuard();
        return this;
    }

    public DeltaBytes Int64(long value)
    {
        // 8-byte value followed by ONE guard (not one per 32-bit half).
        WriteRaw32(unchecked((int)(value & 0xFFFFFFFF)));
        WriteRaw32(unchecked((int)((value >> 32) & 0xFFFFFFFF)));
        MaybeGuard();
        return this;
    }

    /// <summary>BEGIN tag + size word (the size value is not validated by the
    /// reader except for the skip path; use any positive sentinel for walked
    /// containers).</summary>
    public DeltaBytes Begin(int size) => Int32(TagBegin).Int32(size);

    public DeltaBytes End() => Int32(TagEnd);

    /// <summary>A field index entry (proto field number).</summary>
    public DeltaBytes FieldIndex(int index) => Int32(index);
}
