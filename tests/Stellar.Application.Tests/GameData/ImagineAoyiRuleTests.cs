using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.GameData;

/// <summary>
/// Pins <see cref="ImagineAoyiRule"/> — resolution of Battle-Imagine SUMMON skills
/// (SlotPositionId [0]/[6], no 7/8 flag) to the aoyi identity skill id.
/// <para>
/// All ids and mappings below are table facts from
/// <c>data/StarResonanceData/tables/SkillTable.json</c> + <c>SkillAoyiTable.json</c>
/// (verified 2026-08-13 against prod run jp/RXALtMH6J3): monster summon skills are
/// <c>MonsterId*100+NN</c> and <c>SkillAoyiTable[aoyiId].MonsterId</c> closes the loop;
/// companion arcanes carry no closure and ride the curated map.
/// </para>
/// </summary>
public sealed class ImagineAoyiRuleTests
{
    // SkillAoyiTable MonsterId -> aoyi id, as dumped from the live table (subset).
    private static readonly Dictionary<int, int> AoyiByMonster = new()
    {
        [10084] = 3944,   // Celestial Flier — "Arcane! Azure Gale"
        [10077] = 3942,   // Venobzzar Incubator — "Arcane! Poison Explosion"
        [10086] = 3946,   // Goblin King — "Arcane! Goblin March"
        [1110] = 3971,    // Kartgriff — "Arcane! Superconductor Surge"
        [1401] = 3969,    // Igoreus — collision band, see caller-constraint test
    };

    [Theory]
    [InlineData(1008440, 10084)]   // Celestial Flier "Arcane! Azure Gale", slots [0]
    [InlineData(1007740, 10077)]   // Venobzzar "Arcane! Poison Explosion", slots [0]
    [InlineData(1007741, 10077)]   // Venobzzar field-marker skill, slots [0]
    [InlineData(111069, 1110)]     // Kartgriff "Arcane! Superconductor Surge", slots [0]
    [InlineData(300004301, 3000043)]   // 9-digit companion-monster band still fits in int
    public void CandidateMonsterId_decomposes_summon_ids(int skillId, int monsterId)
        => Assert.Equal(monsterId, ImagineAoyiRule.CandidateMonsterId(skillId));

    [Theory]
    [InlineData(0)]
    [InlineData(3944)]     // aoyi identity skill itself
    [InlineData(99_999)]   // at the boundary — too small to be MonsterId*100+NN
    public void CandidateMonsterId_rejects_small_ids(int skillId)
        => Assert.Equal(0, ImagineAoyiRule.CandidateMonsterId(skillId));

    [Theory]
    [InlineData(2900715, 3950)]   // Boyce "Arcane! Allscape Sublimation" variant, slots [0]
    [InlineData(2900740, 3950)]   // Boyce "Arcane! Allscape Sublimation", slots [0]
    [InlineData(2900840, 3948)]   // Rorola "Arcane! Divine Assurance", slots [6]
    [InlineData(2900940, 3951)]   // Fafala "Arcane! Guardian's Boundary", slots [0]
    [InlineData(2900942, 3951)]   // Fafala "Arcane! Guardian's Boundary" variant, slots [0]
    public void MapCompanionArcane_curated_rows(int skillId, int aoyiId)
        => Assert.Equal(aoyiId, ImagineAoyiRule.MapCompanionArcane(skillId));

    [Theory]
    [InlineData(2900240)]   // Airona — [7,8]-flagged, resolves via the normal path
    [InlineData(2900340)]   // Tina — [7,8]-flagged
    [InlineData(2900540)]   // Olvera — [7,8]-flagged
    [InlineData(2900640)]   // Tatta — [7,8]-flagged
    [InlineData(3210021)]   // class TRANSFORM skill — correctly not an imagine
    [InlineData(1008440)]   // monster summon skill — closure's job, not the curated map's
    public void MapCompanionArcane_ignores_everything_else(int skillId)
        => Assert.Equal(0, ImagineAoyiRule.MapCompanionArcane(skillId));

    [Fact]
    public void ResolveSummonAoyi_monster_closure_resolves_to_aoyi_identity()
    {
        Assert.Equal(3944, ImagineAoyiRule.ResolveSummonAoyi(1008440, AoyiByMonster));
        Assert.Equal(3942, ImagineAoyiRule.ResolveSummonAoyi(1007740, AoyiByMonster));
        Assert.Equal(3971, ImagineAoyiRule.ResolveSummonAoyi(111069, AoyiByMonster));
    }

    [Fact]
    public void ResolveSummonAoyi_curated_rows_need_no_table()
        => Assert.Equal(3948, ImagineAoyiRule.ResolveSummonAoyi(2900840, new Dictionary<int, int>()));

    [Fact]
    public void ResolveSummonAoyi_unmapped_band_is_negative()
        => Assert.Equal(0, ImagineAoyiRule.ResolveSummonAoyi(1234567, AoyiByMonster));

    /// <summary>
    /// Documents the caller constraint: the closure cannot distinguish a leveled PLAYER id from a
    /// monster summon skill in the same numeric band. 140116 is BOTH Windborne Grace lv16 (has its
    /// own SkillFightLevelTable row, SkillId 1401) AND Igoreus monster skill "Thunder Vortex" —
    /// the rule alone maps it to Igoreus' aoyi, so <c>GetImagineForSkill</c> MUST skip ids that
    /// carry an own SkillFightLevelTable row (its <c>baseId == 0</c> gate). Weakening that gate
    /// turns every Windborne Grace cast into a phantom imagine cast.
    /// </summary>
    [Fact]
    public void ResolveSummonAoyi_collision_band_resolves_hence_callers_must_gate_on_fight_level_row()
        => Assert.Equal(3969, ImagineAoyiRule.ResolveSummonAoyi(140116, AoyiByMonster));
}
