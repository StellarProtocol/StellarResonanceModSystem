using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Party.Protobuf;

public sealed class NotifyJoinTeamReaderTests
{
    [Fact]
    public void TryRead_BaseInfo_AndOneMember_AndOneFastSync()
    {
        var baseInfo = new WireBytes()
            .Tag(1, 0).Varint(7777UL)
            .Tag(3, 0).Varint(111UL)
            .Tag(8, 0).Varint(0UL)
            .ToArray();

        var member1 = new WireBytes()
            .Tag(1, 0).Varint(111UL)
            .Tag(5, 0).Varint(1UL)
            .ToArray();

        var fastInner = new WireBytes()
            .Tag(1, 0).Varint(111UL)
            .Tag(4, 0).Varint(9000UL)
            .ToArray();
        var mapEntry = new WireBytes()
            .Tag(1, 0).Varint(111UL)
            .Tag(2, 2).LengthDelimited(fastInner)
            .ToArray();

        var payload = new WireBytes()
            .Tag(1, 2).LengthDelimited(baseInfo)
            .Tag(2, 2).LengthDelimited(member1)
            .Tag(6, 2).LengthDelimited(mapEntry)
            .ToArray();

        var ok = NotifyJoinTeamReader.TryRead(payload, out var msg);

        Assert.True(ok);
        Assert.Equal(7777L, msg.PartyId);
        Assert.Equal(111L,  msg.LeaderCharId);
        Assert.Equal(PartyType.Regular5, msg.PartyType);
        Assert.Single(msg.Roster);
        Assert.Equal(111L, msg.Roster[0].CharId);
        Assert.NotNull(msg.Roster[0].FastSync);
        Assert.Equal(9000L, msg.Roster[0].FastSync!.Hp);
    }
}
