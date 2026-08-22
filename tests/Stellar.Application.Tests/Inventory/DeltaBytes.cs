using System.IO;

namespace Stellar.Application.Tests.Inventory;

/// <summary>
/// Little-endian builder for the game's custom container-delta format, used to
/// drive <c>ContainerDirtyDeltaReader</c> in tests with bytes produced by an
/// INDEPENDENT encoder (no risk of testing the parser against the same code
/// that produced the input). Mirrors the BPSR-B BlobReader layout: signed
/// little-endian i32/i64, BEGIN(-2)/END(-3)/skip(-4)/add-only(-1) sentinels.
///
/// <para><b>Wire shape of a nested field blob</b> (authoritative decoder:
/// StarResonanceData <c>lua/zcontainer/char_serialize.lua</c> <c>mergeData</c>,
/// ~line 1966): a NON-EMPTY nested container is
/// <c>[index][-2][size][body][-3]</c> where <c>size</c> EXCLUDES the trailing
/// END tag — the game's unknown-field bail jumps <c>offset + size</c> and then
/// REQUIRES the next i32 to be -3 ("Invalid end tag"). An EMPTY nested
/// container is <c>[index][-2][-3]</c> with NO trailing tag. Hand-built test
/// buffers must follow this exactly: a skipped non-empty body needs its own
/// trailing <see cref="End"/>, or the test encodes the parser's assumption
/// instead of the wire (which is how the walk-dies-after-first-skip defect —
/// owner run <c>sea/pNhmVQvVmV</c> — stayed green).</para>
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

    /// <summary>Builds a minimal CharSerialize buffer containing a single field index
    /// with an empty nested container — for testing top-level field scans like TouchesEquip,
    /// TouchesTalents, etc. Wire-accurate empty shape: <c>[index][-2][-3]</c>, NO trailing
    /// tag (char_serialize.lua mergeData returns straight after <c>size == -3</c>).</summary>
    public static byte[] CharSerializeWithField(int fieldNum)
    {
        return new DeltaBytes()
            .Begin(0)           // CharSerialize container
            .FieldIndex(fieldNum)
            .Begin(TagEnd)      // empty nested container for this field (size == END)
            .End()
            .ToArray();
    }

    /// <summary>Builds a CharSerialize buffer containing TWO top-level fields:
    /// <paramref name="skippedFieldNum"/> first, carrying a small NON-EMPTY nested container
    /// (so the reader's skip-unknown-field path — not just the empty-container shortcut — is
    /// exercised), followed by <paramref name="targetFieldNum"/> with an empty nested container.
    /// For testing that a top-level field scan (e.g. TouchesTalents) still matches a target field
    /// that comes AFTER a field it had to skip over. Wire-accurate non-empty shape:
    /// <c>[index][-2][size][body][-3]</c> with <c>size</c> EXCLUSIVE of the trailing END tag
    /// (char_serialize.lua mergeData, see class doc) — the trailing <see cref="End"/> here is
    /// load-bearing: without it the builder encodes the parser's wrong assumption, not the wire.</summary>
    public static byte[] CharSerializeWithFieldThenField(int skippedFieldNum, int targetFieldNum)
    {
        return new DeltaBytes()
            .Begin(0)                             // CharSerialize container
            .FieldIndex(skippedFieldNum)
            .Begin(8).Int32(11).Int32(22).End()   // non-empty skippable body (size 8, EXCLUSIVE) + END
            .FieldIndex(targetFieldNum)
            .Begin(TagEnd)                        // empty nested container for the target field
            .End()
            .ToArray();
    }
}
