using Stellar.Abstractions.Domain;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Party.Protobuf;

public sealed class PositionReaderTests
{
    [Fact]
    public void TryRead_AllThreeFloats_ParsesXYZ()
    {
        var x = System.BitConverter.GetBytes(1.5f);
        var y = System.BitConverter.GetBytes(-2.25f);
        var z = System.BitConverter.GetBytes(99.0f);

        var payload = new WireBytes()
            .Tag(1, 5).Raw(x[0]).Raw(x[1]).Raw(x[2]).Raw(x[3])
            .Tag(2, 5).Raw(y[0]).Raw(y[1]).Raw(y[2]).Raw(y[3])
            .Tag(3, 5).Raw(z[0]).Raw(z[1]).Raw(z[2]).Raw(z[3])
            .ToArray();

        var ok = PositionReader.TryRead(payload, out var pos);

        Assert.True(ok);
        Assert.Equal(1.5f,   pos.X);
        Assert.Equal(-2.25f, pos.Y);
        Assert.Equal(99.0f,  pos.Z);
    }

    [Fact]
    public void TryRead_EmptyPayload_ReturnsZeroPosition()
    {
        var ok = PositionReader.TryRead(new byte[0], out var pos);
        Assert.True(ok);
        Assert.Equal(Position3D.Zero, pos);
    }
}
