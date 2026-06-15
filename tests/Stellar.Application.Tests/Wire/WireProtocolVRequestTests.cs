using System;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Unit tests for <see cref="WireProtocol.TryReadVRequest"/> — the shared
/// v_request envelope unwrap helper extracted from the 5 copy-pasted sites in
/// <see cref="Stellar.Infrastructure.Game.PandaPartyStubProbe"/>.
///
/// <para>
/// The v_request convention used by GrpcTeamNtf wraps each method payload as:
/// <code>
/// message SomeGrpcTeamNtfMethod {
///   SomeRequest v_request = 1;   // field 1, wire-type 2 (length-delimited)
/// }
/// </code>
/// So the outer payload always starts with tag 0x0A (field=1, wire=2) followed
/// by a varint length and then the inner bytes.
/// </para>
///
/// <para>
/// The exact decode bytes mirror the 5 hand-rolled sites in
/// <c>PandaPartyStubProbe.cs</c> at lines ~389-466:
/// <c>TryReadTag(field!=1||wire!=2) → false</c>;
/// <c>TryReadLengthDelimited → inner span</c>.
/// </para>
/// </summary>
public sealed class WireProtocolVRequestTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build a valid v_request envelope: tag 0x0A (field=1, wire=2) +
    /// varint length + <paramref name="inner"/> bytes.
    /// </summary>
    private static byte[] MakeVRequest(byte[] inner)
    {
        // length as varint (single byte if < 128)
        var lenVarint = EncodeVarint((ulong)inner.Length);
        var result = new byte[1 + lenVarint.Length + inner.Length];
        result[0] = 0x0A; // tag: field=1, wire=2
        lenVarint.CopyTo(result, 1);
        inner.CopyTo(result, 1 + lenVarint.Length);
        return result;
    }

    private static byte[] EncodeVarint(ulong value)
    {
        var buf = new System.Collections.Generic.List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            buf.Add(b);
        } while (value != 0);
        return buf.ToArray();
    }

    // -----------------------------------------------------------------------
    // Happy path
    // -----------------------------------------------------------------------

    [Fact]
    public void TryReadVRequest_ValidFieldOne_WireTwo_ReturnsInnerSpan()
    {
        var innerBytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var payload = MakeVRequest(innerBytes);

        var ok = WireProtocol.TryReadVRequest(payload, out var inner);

        Assert.True(ok);
        Assert.Equal(innerBytes, inner.ToArray());
    }

    [Fact]
    public void TryReadVRequest_EmptyInner_ReturnsEmptySpan()
    {
        // A v_request wrapping a zero-length submessage is valid (e.g. dissolve)
        var payload = MakeVRequest(Array.Empty<byte>());

        var ok = WireProtocol.TryReadVRequest(payload, out var inner);

        Assert.True(ok);
        Assert.Equal(0, inner.Length);
    }

    [Fact]
    public void TryReadVRequest_LargeInner_ReturnsAllInnerBytes()
    {
        // Simulate a 150-byte payload (requires multi-byte varint length)
        var innerBytes = new byte[150];
        for (int i = 0; i < 150; i++) innerBytes[i] = (byte)(i & 0xFF);
        var payload = MakeVRequest(innerBytes);

        var ok = WireProtocol.TryReadVRequest(payload, out var inner);

        Assert.True(ok);
        Assert.Equal(innerBytes, inner.ToArray());
    }

    // -----------------------------------------------------------------------
    // Wrong field number
    // -----------------------------------------------------------------------

    [Fact]
    public void TryReadVRequest_WrongField_Two_WireTwo_ReturnsFalse()
    {
        // field=2, wire=2 → tag = (2<<3)|2 = 0x12
        var payload = new byte[] { 0x12, 0x04, 0x01, 0x02, 0x03, 0x04 };

        var ok = WireProtocol.TryReadVRequest(payload, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryReadVRequest_FieldZero_ReturnsFalse()
    {
        // field=0, wire=2 → tag = 0x02
        var payload = new byte[] { 0x02, 0x01, 0xFF };

        var ok = WireProtocol.TryReadVRequest(payload, out _);

        Assert.False(ok);
    }

    // -----------------------------------------------------------------------
    // Wrong wire type
    // -----------------------------------------------------------------------

    [Fact]
    public void TryReadVRequest_FieldOne_WireTypeVarint_ReturnsFalse()
    {
        // field=1, wire=0 (varint) → tag = 0x08
        var payload = new byte[] { 0x08, 0x01 }; // varint value 1

        var ok = WireProtocol.TryReadVRequest(payload, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryReadVRequest_FieldOne_WireType64bit_ReturnsFalse()
    {
        // field=1, wire=1 (64-bit) → tag = 0x09
        var payload = new byte[] { 0x09, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

        var ok = WireProtocol.TryReadVRequest(payload, out _);

        Assert.False(ok);
    }

    // -----------------------------------------------------------------------
    // Truncated / malformed
    // -----------------------------------------------------------------------

    [Fact]
    public void TryReadVRequest_EmptyPayload_ReturnsFalse()
    {
        var ok = WireProtocol.TryReadVRequest(ReadOnlySpan<byte>.Empty, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryReadVRequest_TagOnlyTruncated_ReturnsFalse()
    {
        // Only the tag byte, no length varint
        var payload = new byte[] { 0x0A };

        var ok = WireProtocol.TryReadVRequest(payload, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryReadVRequest_LengthClaimsMoreBytesThanAvailable_ReturnsFalse()
    {
        // tag=0x0A, length=10 (0x0A), but only 3 inner bytes follow
        var payload = new byte[] { 0x0A, 0x0A, 0x01, 0x02, 0x03 };

        var ok = WireProtocol.TryReadVRequest(payload, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryReadVRequest_TruncatedLengthVarint_ReturnsFalse()
    {
        // tag=0x0A, then continuation byte 0x80 with no following byte
        var payload = new byte[] { 0x0A, 0x80 };

        var ok = WireProtocol.TryReadVRequest(payload, out _);

        Assert.False(ok);
    }
}
