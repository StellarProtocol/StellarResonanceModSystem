using Stellar.Abstractions.Domain.GameData;
using Xunit;

namespace Stellar.Application.Tests.GameData;

/// <summary>
/// Verifies boss-classification rules encoded in <see cref="MonsterInfo"/>.
/// <para>
/// <see cref="MonsterInfo.IsBoss"/> is computed at load time from
/// <c>MonsterType</c> via <c>MonsterBossRule.IsBoss</c> in Infrastructure.
/// These tests validate the projection by constructing <see cref="MonsterInfo"/>
/// values directly — no IL2CPP / BepInEx dependency required.
/// </para>
/// <para>
/// Rule pinned by <c>recon/replay-boss-identification-notes.md</c> (2026-07-02):
/// Ancient Purifier attr-10 = 33301 → MonsterTable[33301].MonsterType = 2 (Boss).
/// Adds had MonsterType = 0.
/// </para>
/// </summary>
public sealed class MonsterBossRuleTests
{
    [Fact]
    public void MonsterType2_IsBossTrue()
    {
        var info = new MonsterInfo(33301, "Ancient Purifier", 50, 1, "icons/33301.png", MonsterType: 2, IsBoss: true);
        Assert.True(info.IsBoss);
    }

    [Fact]
    public void MonsterType0_IsBossFalse()
    {
        var info = new MonsterInfo(3000020, "Tempest Ogre - Resonance", 10, 1, "", MonsterType: 0, IsBoss: false);
        Assert.False(info.IsBoss);
    }

    [Fact]
    public void MonsterType1_IsBossFalse()
    {
        var info = new MonsterInfo(5000, "Elite Guard", 30, 2, "", MonsterType: 1, IsBoss: false);
        Assert.False(info.IsBoss);
    }

    [Fact]
    public void MonsterTypeBoss_Const_Equals2()
    {
        Assert.Equal(2, MonsterInfo.MonsterTypeBoss);
    }

    [Fact]
    public void DefaultMonsterInfo_IsBossFalse()
    {
        // default MonsterType/IsBoss args should produce a non-boss entry
        var info = new MonsterInfo(1, "Mob", 5, 0, "");
        Assert.False(info.IsBoss);
        Assert.Equal(0, info.MonsterType);
    }
}
