using Stellar.Application.Services;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Party.Protobuf;

public sealed class TeamMemberFastSyncDataReaderTests
{
    [Fact]
    public void TryRead_AllFields_ReturnsCharIdAndPayload()
    {
        var x = System.BitConverter.GetBytes(10.5f);
        var y = System.BitConverter.GetBytes(20.5f);
        var z = System.BitConverter.GetBytes(30.5f);
        var pos = new WireBytes()
            .Tag(1, 5).Raw(x[0]).Raw(x[1]).Raw(x[2]).Raw(x[3])
            .Tag(2, 5).Raw(y[0]).Raw(y[1]).Raw(y[2]).Raw(y[3])
            .Tag(3, 5).Raw(z[0]).Raw(z[1]).Raw(z[2]).Raw(z[3])
            .ToArray();

        var payload = new WireBytes()
            .Tag(1, 0).Varint(123456789UL)
            .Tag(2, 0).Varint(101UL)
            .Tag(3, 2).LengthDelimited(pos)
            .Tag(4, 0).Varint(8500UL)
            .Tag(5, 0).Varint(10000UL)
            .Tag(6, 0).Varint(2UL)
            .Tag(7, 0).Varint(7UL)
            .ToArray();

        var ok = TeamMemberFastSyncDataReader.TryRead(payload, out var charId, out var data);

        Assert.True(ok);
        Assert.Equal(123456789L, charId);
        Assert.Equal(101,        data.SceneId);
        Assert.Equal(10.5f,      data.Position.X);
        Assert.Equal(8500L,      data.Hp);
        Assert.Equal(10000L,     data.MaxHp);
        Assert.Equal(2,          data.StateRaw);
    }

    [Fact]
    public void TryRead_OnlyCharIdAndHp_OthersDefault()
    {
        var payload = new WireBytes()
            .Tag(1, 0).Varint(42UL)
            .Tag(4, 0).Varint(500UL)
            .ToArray();

        var ok = TeamMemberFastSyncDataReader.TryRead(payload, out var charId, out var data);

        Assert.True(ok);
        Assert.Equal(42L,  charId);
        Assert.Equal(500L, data.Hp);
        Assert.Equal(0L,   data.MaxHp);
        Assert.Equal(0,    data.SceneId);
    }
}
