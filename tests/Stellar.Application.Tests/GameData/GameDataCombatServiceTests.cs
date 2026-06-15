using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public sealed class GameDataCombatServiceTests
{
    [Fact]
    public void GetSkill_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataCombatService();
        svc.LoadSkills(new Dictionary<int, SkillInfo>
        {
            [12345] = new SkillInfo(12345, "Frostbolt", "Deal frost dmg",
                "icons/12345.png", SkillKind.Active, 4000, false),
        });

        var result = svc.GetSkill(12345);

        Assert.NotNull(result);
        Assert.Equal("Frostbolt", result!.Value.Name);
        Assert.Equal(SkillKind.Active, result.Value.Kind);
    }

    [Fact]
    public void GetSkill_ReturnsNull_WhenUnknown()
    {
        var svc = new GameDataCombatService();
        svc.LoadSkills(new Dictionary<int, SkillInfo>());

        var result = svc.GetSkill(999);

        Assert.Null(result);
    }

    [Fact]
    public void GetSkill_ReturnsNull_WhenCacheUnset()
    {
        var svc = new GameDataCombatService();
        // Never called LoadSkills.

        var result = svc.GetSkill(123);

        Assert.Null(result);
    }

    [Fact]
    public void LoadSkills_ReplacesExistingCache_Atomically()
    {
        var svc = new GameDataCombatService();
        svc.LoadSkills(new Dictionary<int, SkillInfo>
        {
            [1] = new SkillInfo(1, "Old", "", "", SkillKind.Active, 0, false),
        });

        svc.LoadSkills(new Dictionary<int, SkillInfo>
        {
            [2] = new SkillInfo(2, "New", "", "", SkillKind.Passive, 0, false),
        });

        Assert.Null(svc.GetSkill(1));
        Assert.Equal("New", svc.GetSkill(2)!.Value.Name);
    }

    [Fact]
    public void GetBuff_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataCombatService();
        svc.LoadBuffs(new Dictionary<int, BuffInfo>
        {
            [42] = new BuffInfo(42, "Chill", "Slows target",
                "icons/42.png", BuffCategory.Control, true),
        });

        var result = svc.GetBuff(42);

        Assert.NotNull(result);
        Assert.True(result!.Value.IsDebuff);
    }

    [Fact]
    public void GetProfession_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataCombatService();
        svc.LoadProfessions(new Dictionary<int, ProfessionInfo>
        {
            [1] = new ProfessionInfo(1, "Frost Mage", "icons/prof1.png",
                false, new[] { 12345, 12346 }),
        });

        var result = svc.GetProfession(1);

        Assert.NotNull(result);
        Assert.Equal("Frost Mage", result!.Value.Name);
    }

    [Fact]
    public void GetTalent_ReturnsNull_BeforeDeferredLoad()
    {
        var svc = new GameDataCombatService();

        // Talent is a deferred table — never loaded in test setup.
        Assert.Null(svc.GetTalent(7));
    }

    [Fact]
    public void GetTalent_ReturnsRow_AfterDeferredLoad()
    {
        var svc = new GameDataCombatService();

        svc.LoadTalents(new Dictionary<int, TalentInfo>
        {
            [7] = new TalentInfo(7, "Icicle Spec", "Frost mastery", "icons/7.png", 1),
        });

        Assert.Equal("Icicle Spec", svc.GetTalent(7)!.Value.Name);
    }

    [Fact]
    public void GetAttribute_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataCombatService();
        svc.LoadAttributes(new Dictionary<int, AttributeInfo>
        {
            [21] = new AttributeInfo(21, "Intellect", "INT", "icons/21.png",
                AttributeGroup.Offensive),
        });

        var result = svc.GetAttribute(21);

        Assert.NotNull(result);
        Assert.Equal(AttributeGroup.Offensive, result!.Value.Group);
    }

    [Fact]
    public void GetDamageAttr_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataCombatService();
        svc.LoadDamageAttrs(new Dictionary<int, DamageAttrInfo>
        {
            [101] = new DamageAttrInfo(101, "Ice Attack", 2, 40),
        });

        Assert.Equal("Ice Attack", svc.GetDamageAttr(101)!.Value.Name);
    }

    [Fact]
    public void GetAttributeProfile_ReturnsRow_WhenKnownByBaseAttrId()
    {
        var svc = new GameDataCombatService();
        // Cache is keyed by the EAttrType BASE attr id (e.g. AttrMaxHp=11320).
        svc.LoadAttributeProfiles(new Dictionary<int, AttributeProfileInfo>
        {
            [11010] = new AttributeProfileInfo(
                AttrId: 11010,
                Name: "Strength",
                Type: 1,
                TypeDisplayName: "Offensive Attributes"),
        });

        var result = svc.GetAttributeProfile(11010);

        Assert.NotNull(result);
        Assert.Equal(11010, result!.Value.AttrId);
        Assert.Equal("Offensive Attributes", result.Value.TypeDisplayName);
    }

    [Fact]
    public void GetAttributeProfile_FallsBack_FromTotalToBase()
    {
        // Plugins typically subscribe to TOTAL variants (e.g. AttrStrengthTotal=
        // 11011) which read the final buffed value; AttributeProfile only stores
        // the BASE entries (11010). GetAttributeProfile falls back to (id - 1).
        var svc = new GameDataCombatService();
        svc.LoadAttributeProfiles(new Dictionary<int, AttributeProfileInfo>
        {
            [11010] = new AttributeProfileInfo(
                AttrId: 11010,
                Name: "Strength",
                Type: 1,
                TypeDisplayName: "Offensive Attributes"),
        });

        var result = svc.GetAttributeProfile(11011);  // Total variant

        Assert.NotNull(result);
        Assert.Equal(11010, result!.Value.AttrId);
        Assert.Equal("Strength", result.Value.Name);
    }

    [Fact]
    public void GetAttributeProfile_ReturnsNull_WhenUnknown()
    {
        var svc = new GameDataCombatService();
        svc.LoadAttributeProfiles(new Dictionary<int, AttributeProfileInfo>());

        Assert.Null(svc.GetAttributeProfile(999));
    }
}
