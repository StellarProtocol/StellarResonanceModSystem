using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class BuffChangeReaderTests
{
    [Fact]
    public void TryRead_MapsLayerDurationCreateTime()
    {
        var payload = new WireBytes()
            .Tag(1, 0).Varint(4)                    // Layer
            .Tag(2, 0).Varint(8_000)                // Duration
            .Tag(3, 0).Varint(1_700_000_000_500UL)  // CreateTime
            .ToArray();

        var ok = BuffChangeReader.TryRead(payload, out var layer, out var dur, out var create);

        Assert.True(ok);
        Assert.Equal(4, layer);
        Assert.Equal(8_000, dur);
        Assert.Equal(1_700_000_000_500L, create);
    }
}
