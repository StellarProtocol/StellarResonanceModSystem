using Stellar.Abstractions.Domain;
using Xunit;

namespace Stellar.Application.Tests.Combat;

public sealed class EntityIdTests
{
    [Fact]
    public void None_IsZeroValue_AndIsNoneIsTrue()
    {
        Assert.Equal(0L, EntityId.None.Value);
        Assert.True(EntityId.None.IsNone);
    }

    [Theory]
    [InlineData(0x0000_0000_0001_0280L, true)]  // low 16 = 640
    [InlineData(0x0000_0000_0001_0040L, false)] // low 16 = 64 (monster)
    [InlineData(0L, false)]                     // None is not a player
    public void IsPlayer_DependsOnLow16Equals640(long value, bool expected)
        => Assert.Equal(expected, new EntityId(value).IsPlayer);

    [Theory]
    [InlineData(0x0000_0000_0001_0040L, true)]   // low 16 = 64
    [InlineData(0x0000_0000_0001_8040L, true)]   // low 16 = 32832
    [InlineData(0x0000_0000_0001_0280L, false)]  // player
    public void IsMonster_DependsOnLow16Equals64Or32832(long value, bool expected)
        => Assert.Equal(expected, new EntityId(value).IsMonster);

    [Theory]
    [InlineData(0x0000_0000_002A_0280L, 0x002A)]   // uid=42 player
    [InlineData(0x0000_0000_FFFF_0040L, 0xFFFF)]   // uid=65535 monster
    public void Uid_IsHighBitsShiftedDown(long value, int expected)
        => Assert.Equal(expected, new EntityId(value).Uid);
}
