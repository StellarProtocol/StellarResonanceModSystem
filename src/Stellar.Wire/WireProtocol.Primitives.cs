using System;

namespace Stellar.Wire;

/// <summary>
/// Protobuf wire-format primitive readers. Defensive Try* pattern throughout —
/// any malformed input causes a short-circuit return (never throws). These are
/// the building blocks consumed by every higher-level parser in
/// <see cref="WireProtocol"/>'s ChitChat partial.
/// </summary>
public static partial class WireProtocol
{
    /// <summary>
    /// Read a base-128 varint into <paramref name="value"/>. Advances
    /// <paramref name="pos"/> past the consumed bytes. Returns false on EOF or
    /// overflow (varint longer than 10 bytes).
    /// </summary>
    public static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }

    /// <summary>
    /// Read a protobuf tag (field_number, wire_type) at <paramref name="pos"/>.
    /// </summary>
    public static bool TryReadTag(ReadOnlySpan<byte> data, ref int pos, out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;
        if (!TryReadVarint(data, ref pos, out var tag)) return false;
        fieldNumber = (int)(tag >> 3);
        wireType = (int)(tag & 0x07);
        return true;
    }

    /// <summary>
    /// Read a UTF-8 length-prefixed string. Advances <paramref name="pos"/>
    /// past the bytes consumed. Returns false on malformed/truncated input.
    /// </summary>
    public static bool TryReadString(ReadOnlySpan<byte> data, ref int pos, out string value)
    {
        value = string.Empty;
        if (!TryReadVarint(data, ref pos, out var len)) return false;
        if (len > int.MaxValue) return false;
        int n = (int)len;
        if (n < 0 || n > data.Length - pos) return false;
        value = System.Text.Encoding.UTF8.GetString(data.Slice(pos, n));
        pos += n;
        return true;
    }

    /// <summary>
    /// Read a length-delimited sub-message into <paramref name="inner"/>.
    /// <paramref name="pos"/> is <c>scoped</c> so the compiler knows the returned
    /// span cannot capture the position ref — callers may pass a plain local both
    /// by ref here and receive <paramref name="inner"/> into an outer-scope span.
    /// </summary>
    public static bool TryReadLengthDelimited(ReadOnlySpan<byte> data, scoped ref int pos, out ReadOnlySpan<byte> inner)
    {
        inner = default;
        if (!TryReadVarint(data, ref pos, out var len)) return false;
        if (len > int.MaxValue) return false;
        int n = (int)len;
        if (n < 0 || n > data.Length - pos) return false;
        inner = data.Slice(pos, n);
        pos += n;
        return true;
    }

    /// <summary>
    /// Unwrap the <c>v_request</c> envelope used by GrpcTeamNtf methods.
    /// Each GrpcTeamNtf method wraps its payload in a single field 1 /
    /// wire-type 2 (length-delimited) outer message:
    /// <code>
    /// message SomeGrpcTeamNtfMethod {
    ///   SomeRequest v_request = 1;
    /// }
    /// </code>
    /// Returns <see langword="true"/> and sets <paramref name="inner"/> to the
    /// inner sub-message span when the first tag is (field=1, wire=2) and the
    /// declared inner length fits within <paramref name="payload"/>. Returns
    /// <see langword="false"/> (and leaves <paramref name="inner"/> empty) when
    /// the tag is absent, has the wrong field number, has the wrong wire type,
    /// or the inner length is truncated. Never throws.
    ///
    /// <para>
    /// This is the shared extract of the 5 copy-pasted unwrap blocks that were
    /// in <c>PandaPartyStubProbe</c> at lines ~389-466. The logic is exactly:
    /// <c>TryReadTag → field!=1||wire!=2 → false</c>;
    /// <c>TryReadLengthDelimited → inner</c>.
    /// </para>
    /// </summary>
    public static bool TryReadVRequest(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> inner)
    {
        inner = default;
        // Step 1: read the tag varint and check field=1, wire=2.
        // Inlined rather than delegating to TryReadTag + TryReadLengthDelimited.
        // The original constraint (C# could not pass a local 'int pos' both by ref
        // and return a ReadOnlySpan out parameter) was lifted by the scoped-ref
        // signature on TryReadLengthDelimited; the inline form is kept to avoid an
        // extra indirection. Logic is identical to the 5 original call sites.
        if (payload.Length < 1) return false;
        int pos = 0;

        // Read the tag varint.
        ulong tag = 0;
        int shift = 0;
        while (pos < payload.Length)
        {
            byte b = payload[pos++];
            tag |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) goto tagRead;
            shift += 7;
            if (shift > 63) return false;
        }
        return false; // truncated varint

        tagRead:
        int field = (int)(tag >> 3);
        int wire  = (int)(tag & 0x07);
        if (field != 1 || wire != 2) return false;

        // Step 2: read the length-delimited inner span.
        ulong len = 0;
        shift = 0;
        while (pos < payload.Length)
        {
            byte b = payload[pos++];
            len |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) goto lenRead;
            shift += 7;
            if (shift > 63) return false;
        }
        return false; // truncated length varint

        lenRead:
        if (len > int.MaxValue) return false;
        int n = (int)len;
        if (n < 0 || n > payload.Length - pos) return false;
        inner = payload.Slice(pos, n);
        return true;
    }

    /// <summary>
    /// Skip a wire-type-typed field whose tag has already been consumed.
    /// </summary>
    public static bool SkipField(ReadOnlySpan<byte> data, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: // varint
                return TryReadVarint(data, ref pos, out _);
            case 1: // 64-bit fixed
                if (pos + 8 > data.Length) return false;
                pos += 8;
                return true;
            case 2: // length-delimited
                if (!TryReadVarint(data, ref pos, out var l)) return false;
                if (l > int.MaxValue) return false;
                int n = (int)l;
                if (n < 0 || n > data.Length - pos) return false;
                pos += n;
                return true;
            case 5: // 32-bit fixed
                if (pos + 4 > data.Length) return false;
                pos += 4;
                return true;
            default:
                // Unknown / group wire types (3, 4) are obsolete in proto3 and
                // shouldn't appear. Fail closed.
                return false;
        }
    }
}
