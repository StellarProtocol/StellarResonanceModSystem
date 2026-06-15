using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class BuffInfoReaderTests
{
    [Fact]
    public void TryRead_MapsAllFields()
    {
        var payload = new WireBytes()
            .Tag(1, 0).Varint(111)                  // BuffUuid
            .Tag(2, 0).Varint(2110056)              // BaseId
            .Tag(3, 0).Varint(5)                    // Level
            .Tag(6, 0).Varint(1_700_000_000_000UL)  // CreateTime
            .Tag(7, 0).Varint(0xABCD0280UL)         // FireUuid (low16=0x280 → IsPlayer)
            .Tag(8, 0).Varint(3)                    // Layer
            .Tag(10, 0).Varint(7)                   // Count
            .Tag(11, 0).Varint(6_000)               // Duration
            .ToArray();

        var ok = BuffInfoReader.TryRead(payload, out var b);

        Assert.True(ok);
        Assert.Equal(111,     b.BuffUuid);
        Assert.Equal(2110056, b.BaseId);
        Assert.Equal(5,       b.Level);
        Assert.Equal(1_700_000_000_000L, b.CreateTimeMs);
        Assert.True(b.FirerId.IsPlayer);
        Assert.Equal(3,       b.Layer);
        Assert.Equal(7,       b.Stacks);
        Assert.Equal(6_000,   b.DurationMs);
    }

    [Fact]
    public void TryRead_Empty_ReturnsZeroedBuff()
    {
        Assert.True(BuffInfoReader.TryRead(System.ReadOnlySpan<byte>.Empty, out var b));
        Assert.Equal(0, b.BaseId);
    }
}
