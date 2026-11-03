using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Hand-built WorldNtf notify 43 <c>SyncServerTime</c> bodies driving <see cref="SyncServerTimeReader"/>:
///   SyncServerTime { int64 client_milliseconds = 1 (our PC time echoed); int64 server_milliseconds = 2 }
/// The anchor must be field 2 — field 1 is our own clock (KB Server-Clock-and-Ping-HUD.md).
/// </summary>
public sealed class SyncServerTimeReaderTests
{
    private const long Client = 1_793_000_000_000L;
    private const long Server = 1_793_000_001_720L;

    [Fact]
    public void Field2_present_anchors_server_milliseconds()
    {
        var payload = new WireBytes()
            .Tag(1, 0).Varint((ulong)Client)
            .Tag(2, 0).Varint((ulong)Server)
            .ToArray();

        Assert.True(SyncServerTimeReader.TryRead(payload, out var t));
        Assert.Equal(Server, t);
    }

    [Fact]
    public void Field2_before_field1_still_picks_field2()
    {
        var payload = new WireBytes()
            .Tag(2, 0).Varint((ulong)Server)
            .Tag(1, 0).Varint((ulong)Client)
            .ToArray();

        Assert.True(SyncServerTimeReader.TryRead(payload, out var t));
        Assert.Equal(Server, t);
    }

    [Fact]
    public void Field2_missing_falls_back_to_field1()
    {
        var payload = new WireBytes().Tag(1, 0).Varint((ulong)Client).ToArray();

        Assert.True(SyncServerTimeReader.TryRead(payload, out var t));
        Assert.Equal(Client, t);
    }

    [Fact]
    public void Field2_zero_falls_back_to_field1()
    {
        var payload = new WireBytes()
            .Tag(1, 0).Varint((ulong)Client)
            .Tag(2, 0).Varint(0)
            .ToArray();

        Assert.True(SyncServerTimeReader.TryRead(payload, out var t));
        Assert.Equal(Client, t);
    }

    [Fact]
    public void Unknown_fields_are_skipped()
    {
        var payload = new WireBytes()
            .Tag(3, 2).LengthDelimited(new byte[] { 1, 2, 3 })
            .Tag(1, 0).Varint((ulong)Client)
            .Tag(4, 0).Varint(99)
            .Tag(2, 0).Varint((ulong)Server)
            .ToArray();

        Assert.True(SyncServerTimeReader.TryRead(payload, out var t));
        Assert.Equal(Server, t);
    }

    [Fact]
    public void Empty_payload_returns_false()
    {
        Assert.False(SyncServerTimeReader.TryRead(System.ReadOnlySpan<byte>.Empty, out var t));
        Assert.Equal(0, t);
    }
}
