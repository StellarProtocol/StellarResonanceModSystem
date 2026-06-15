using Stellar.Abstractions.Domain;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Party.Protobuf;

public sealed class NoticeUpdateTeamInfoReaderTests
{
    [Fact]
    public void TryRead_BaseInfoOnly_PopulatesSnapshot()
    {
        var baseInfo = new WireBytes()
            .Tag(1, 0).Varint(999UL)
            .Tag(3, 0).Varint(42UL)
            .Tag(8, 0).Varint(1UL)
            .ToArray();
        var payload = new WireBytes()
            .Tag(1, 2).LengthDelimited(baseInfo)
            .ToArray();

        var ok = NoticeUpdateTeamInfoReader.TryRead(payload, out var snap);

        Assert.True(ok);
        Assert.Equal(999L, snap.PartyId);
        Assert.Equal(42L,  snap.LeaderCharId);
        Assert.Equal(PartyType.Raid20, snap.PartyType);
        Assert.Empty(snap.Roster);
    }
}
