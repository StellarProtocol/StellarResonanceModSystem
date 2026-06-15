using Stellar.Application.Services;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Party.Protobuf;

public sealed class TeamMemDataReaderTests
{
    [Fact]
    public void TryRead_TopLevelFields_PopulatesCharIdAndSocial()
    {
        var basicData = new WireBytes()
            .Tag(3, 2).String("Alice")   // BasicDataReader: name = field 3 (confirmed against stru_basic_data.proto)
            .Tag(6, 0).Varint(42UL)      // BasicDataReader: level = field 6
            .ToArray();
        var professionData = new WireBytes()
            .Tag(1, 0).Varint(3UL)
            .ToArray();
        var social = new WireBytes()
            .Tag(1, 2).LengthDelimited(basicData)
            .Tag(4, 2).LengthDelimited(professionData)
            .ToArray();

        var payload = new WireBytes()
            .Tag(1, 0).Varint(12345UL)
            .Tag(2, 0).Varint(1700000000UL)
            .Tag(5, 0).Varint(1UL)
            .Tag(6, 0).Varint(101UL)
            .Tag(8, 0).Varint(2UL)
            .Tag(9, 2).LengthDelimited(social)
            .ToArray();

        var ok = TeamMemDataReader.TryRead(payload, out var roster);

        Assert.True(ok);
        Assert.Equal(12345L,      roster.CharId);
        Assert.Equal(1700000000,  roster.EnterTimeRaw);
        Assert.Equal(1,           roster.OnlineStatusRaw);
        Assert.Equal(101,         roster.SceneId);
        Assert.Equal(2,           roster.GroupId);
        Assert.NotNull(roster.Social);
        Assert.Equal("Alice", roster.Social!.Name);
        Assert.Equal(42,      roster.Social.Level);
        Assert.Equal(3,       roster.Social.Profession);
        Assert.Equal(2,       roster.Social.GroupId);
    }

    [Fact]
    public void TryRead_NoSocialBlock_LeavesSocialNull()
    {
        var payload = new WireBytes()
            .Tag(1, 0).Varint(99UL)
            .ToArray();

        var ok = TeamMemDataReader.TryRead(payload, out var roster);

        Assert.True(ok);
        Assert.Equal(99L, roster.CharId);
        Assert.Null(roster.Social);
    }
}
