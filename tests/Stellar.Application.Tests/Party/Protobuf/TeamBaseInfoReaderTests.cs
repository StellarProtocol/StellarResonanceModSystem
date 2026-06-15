using Stellar.Abstractions.Domain;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Party.Protobuf;

public sealed class TeamBaseInfoReaderTests
{
    [Fact]
    public void TryRead_AllExposedFields_Parsed()
    {
        var payload = new WireBytes()
            .Tag(1,  0).Varint(7777UL)
            .Tag(3,  0).Varint(8888UL)
            .Tag(7,  0).Varint(1UL)
            .Tag(8,  0).Varint(1UL)
            .Tag(10, 0).Varint(1700000000UL)
            .ToArray();

        var ok = TeamBaseInfoReader.TryRead(payload, out var info);

        Assert.True(ok);
        Assert.Equal(7777L,           info.PartyId);
        Assert.Equal(8888L,           info.LeaderCharId);
        Assert.True(info.IsMatching);
        Assert.Equal(PartyType.Raid20, info.PartyType);
    }

    [Fact]
    public void TryRead_FiveMemberType_MapsToRegular5()
    {
        var payload = new WireBytes()
            .Tag(1, 0).Varint(1UL)
            .Tag(8, 0).Varint(0UL)
            .ToArray();

        var ok = TeamBaseInfoReader.TryRead(payload, out var info);

        Assert.True(ok);
        Assert.Equal(PartyType.Regular5, info.PartyType);
    }
}
