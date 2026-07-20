using System.IO;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Tiny protobuf-wire encoder for test fixtures. Mirrors what the BPSR server
/// would put on the wire so tests can drive <c>WireProtocol</c> with bytes
/// produced by an independent implementation (no risk of testing the parser
/// against the same code that produced the input).
/// </summary>
internal sealed class WireBytes
{
    private readonly MemoryStream _ms = new();

    public byte[] ToArray() => _ms.ToArray();

    /// <summary>Write a protobuf tag: (field_number &lt;&lt; 3) | wire_type, as varint.</summary>
    public WireBytes Tag(int fieldNumber, int wireType)
    {
        // Cast through uint first so the bitwise-or doesn't sign-extend.
        WriteVarint(((ulong)(uint)fieldNumber << 3) | (uint)wireType);
        return this;
    }

    /// <summary>Write a varint (wire type 0).</summary>
    public WireBytes Varint(ulong value)
    {
        WriteVarint(value);
        return this;
    }

    /// <summary>Write a length-prefixed byte block (wire type 2). Length is varint-encoded.</summary>
    public WireBytes LengthDelimited(byte[] payload)
    {
        WriteVarint((ulong)payload.Length);
        _ms.Write(payload, 0, payload.Length);
        return this;
    }

    /// <summary>Write a length-prefixed UTF-8 string (wire type 2).</summary>
    public WireBytes String(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return LengthDelimited(bytes);
    }

    /// <summary>Write a raw byte (escape hatch for crafting malformed inputs).</summary>
    public WireBytes Raw(byte b)
    {
        _ms.WriteByte(b);
        return this;
    }

    /// <summary>Write a little-endian IEEE-754 float32 (wire type 5, the payload after a fixed32 tag).</summary>
    public WireBytes Fixed32(float value)
    {
        System.Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(b, value);
        _ms.Write(b);
        return this;
    }

    private void WriteVarint(ulong value)
    {
        while (value >= 0x80)
        {
            _ms.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        _ms.WriteByte((byte)value);
    }
}
