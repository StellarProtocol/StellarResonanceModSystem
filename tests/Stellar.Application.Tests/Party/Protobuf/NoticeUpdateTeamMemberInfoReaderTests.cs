using Stellar.Application.Services;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Party.Protobuf;

public sealed class NoticeUpdateTeamMemberInfoReaderTests
{
    [Fact]
    public void TryRead_FastAndSocial_BothExtracted()
    {
        var fast = new WireBytes()
            .Tag(1, 0).Varint(111UL)
            .Tag(4, 0).Varint(5000UL)
            .ToArray();
        var social = new WireBytes()
            .Tag(1, 0).Varint(222UL)
            .Tag(8, 0).Varint(1UL)
            .ToArray();

        var payload = new WireBytes()
            .Tag(5, 2).LengthDelimited(fast)
            .Tag(6, 2).LengthDelimited(social)
            .ToArray();

        var ok = NoticeUpdateTeamMemberInfoReader.TryRead(payload, out var msg);

        Assert.True(ok);
        Assert.Single(msg.FastSyncs);
        Assert.Equal(111L, msg.FastSyncs[0].CharId);
        Assert.Equal(5000L, msg.FastSyncs[0].Data.Hp);
        Assert.Single(msg.SocialSyncs);
        Assert.Equal(222L, msg.SocialSyncs[0].CharId);
        Assert.Equal(1, msg.SocialSyncs[0].Roster.GroupId);
    }
}
