using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public sealed class GameDataServiceTests
{
    [Fact]
    public void IsAvailable_FalseByDefault()
    {
        var svc = new GameDataService(new CombatEntityTracker());
        Assert.False(svc.IsAvailable);
    }

    [Fact]
    public void EagerLoad_SuccessfulSnapshot_SetsIsAvailableTrue()
    {
        var (svc, probe) = NewService();
        probe.EagerSnapshot = new GameDataEagerSnapshot
        {
            Skills      = new Dictionary<int, SkillInfo>(),
            Buffs       = new Dictionary<int, BuffInfo>(),
            Professions = new Dictionary<int, ProfessionInfo>
            {
                [1] = new ProfessionInfo(1, "Frost Mage", "i.png", false, new[] { 1 }),
            },
            Attributes  = new Dictionary<int, AttributeInfo>(),
            AttributeProfiles = new Dictionary<int, AttributeProfileInfo>(),
            Items       = new Dictionary<int, ItemInfo>(),
        };

        svc.LoadEager(probe);

        Assert.True(svc.IsAvailable);
        Assert.Equal("Frost Mage", svc.Combat.GetProfession(1)!.Value.Name);
    }

    [Fact]
    public void EagerLoad_PopulatesAttributeProfilesCache()
    {
        var (svc, probe) = NewService();
        probe.EagerSnapshot = new GameDataEagerSnapshot
        {
            Skills      = new Dictionary<int, SkillInfo>(),
            Buffs       = new Dictionary<int, BuffInfo>(),
            Professions = new Dictionary<int, ProfessionInfo>(),
            Attributes  = new Dictionary<int, AttributeInfo>(),
            AttributeProfiles = new Dictionary<int, AttributeProfileInfo>
            {
                // Keyed by the EAttrType BASE attr id (AttrStrength=11010).
                [11010] = new AttributeProfileInfo(
                    AttrId: 11010,
                    Name: "Strength",
                    Type: 1,
                    TypeDisplayName: "Offensive Attributes"),
            },
            Items       = new Dictionary<int, ItemInfo>(),
        };

        svc.LoadEager(probe);

        var profile = svc.Combat.GetAttributeProfile(11010);
        Assert.NotNull(profile);
        Assert.Equal("Offensive Attributes", profile!.Value.TypeDisplayName);
    }

    [Fact]
    public void EagerLoad_FailedProbe_LeavesIsAvailableFalse()
    {
        var (svc, probe) = NewService();
        probe.TryLoadEagerReturns = false;

        svc.LoadEager(probe);

        Assert.False(svc.IsAvailable);
        Assert.Null(svc.Combat.GetProfession(1));
    }

    [Fact]
    public void DeferredQueue_Drains_OneTablePerTick_InEnumOrder()
    {
        var (svc, probe) = NewService();
        probe.EagerSnapshot = EmptyEagerSnapshot();
        probe.DeferredCaches[GameDataTableKind.Talent]
            = new Dictionary<int, TalentInfo> { [7] = new TalentInfo(7, "Icicle Spec", "", "", 1) };
        probe.DeferredCaches[GameDataTableKind.DamageAttr]
            = new Dictionary<int, DamageAttrInfo>();

        svc.LoadEager(probe);

        // First tick: drains Talent (first kind in the enum after the eager set is skipped).
        svc.DrainDeferred(probe);
        Assert.Single(probe.CallOrder);
        Assert.Equal(GameDataTableKind.Talent, probe.CallOrder[0]);
        Assert.Equal("Icicle Spec", svc.Combat.GetTalent(7)!.Value.Name);

        // Second tick: drains DamageAttr.
        svc.DrainDeferred(probe);
        Assert.Equal(2, probe.CallOrder.Count);
        Assert.Equal(GameDataTableKind.DamageAttr, probe.CallOrder[1]);
    }

    [Fact]
    public void DeferredQueue_OnEmpty_DoesNothing()
    {
        var (svc, probe) = NewService();
        probe.EagerSnapshot = EmptyEagerSnapshot();
        svc.LoadEager(probe);

        // Drain through everything (19 deferred kinds), then one more.
        for (var i = 0; i < 19; i++) svc.DrainDeferred(probe);
        var beforeExtraDrain = probe.CallOrder.Count;
        svc.DrainDeferred(probe);

        Assert.Equal(beforeExtraDrain, probe.CallOrder.Count);
    }

    [Fact]
    public void DeferredQueue_FailedLoad_SkipsTableAndContinues()
    {
        var (svc, probe) = NewService();
        probe.EagerSnapshot = EmptyEagerSnapshot();
        // Talent fails (no entry in DeferredCaches), DamageAttr succeeds.
        probe.DeferredCaches[GameDataTableKind.DamageAttr]
            = new Dictionary<int, DamageAttrInfo> { [1] = new DamageAttrInfo(1, "Fire", 1, 10) };
        svc.LoadEager(probe);

        svc.DrainDeferred(probe);  // Talent: TryLoadOne returns false.
        svc.DrainDeferred(probe);  // DamageAttr: TryLoadOne returns true.

        Assert.Equal(2, probe.CallOrder.Count);
        Assert.Null(svc.Combat.GetTalent(7));
        Assert.Equal("Fire", svc.Combat.GetDamageAttr(1)!.Value.Name);
    }

    [Fact]
    public void DeferredQueue_OrderMatchesGameDataTableKindEnum()
    {
        // Enum declares the canonical order: deferred = enum members minus eager set.
        // Verify the queue uses that order.
        var (svc, probe) = NewService();
        probe.EagerSnapshot = EmptyEagerSnapshot();
        foreach (var kind in System.Enum.GetValues<GameDataTableKind>())
        {
            // Provide empty caches for everything so loads "succeed".
            probe.DeferredCaches[kind] = EmptyCacheFor(kind);
        }
        svc.LoadEager(probe);

        // Drain everything.
        for (var i = 0; i < 20; i++) svc.DrainDeferred(probe);

        var expected = new[]
        {
            GameDataTableKind.Talent,
            GameDataTableKind.DamageAttr,
            GameDataTableKind.Equip,
            GameDataTableKind.Weapon,
            GameDataTableKind.Monster,
            GameDataTableKind.Npc,
            GameDataTableKind.Scene,
            GameDataTableKind.Map,
            GameDataTableKind.Quest,
            GameDataTableKind.Dungeon,
            GameDataTableKind.Activity,
            GameDataTableKind.Achievement,
            GameDataTableKind.Title,
            GameDataTableKind.Award,
            GameDataTableKind.EquipRow,
            GameDataTableKind.EquipAttrLib,
            GameDataTableKind.EquipSchoolAttrLib,
            GameDataTableKind.EquipEnchantItem,
            GameDataTableKind.EquipPart,
        };
        Assert.Equal(expected, probe.CallOrder);
    }

    [Fact]
    public void SubInterfaces_ReturnTheSameInstanceEachCall()
    {
        var svc = new GameDataService(new CombatEntityTracker());
        Assert.Same(svc.Combat, svc.Combat);
        Assert.Same(svc.Inventory, svc.Inventory);
        Assert.Same(svc.Equip, svc.Equip);
        Assert.Same(svc.World, svc.World);
        Assert.Same(svc.Progress, svc.Progress);
    }

    private static (GameDataService, StubGameDataProbe) NewService()
        => (new GameDataService(new CombatEntityTracker()), new StubGameDataProbe());

    private static GameDataEagerSnapshot EmptyEagerSnapshot() => new GameDataEagerSnapshot
    {
        Skills            = new Dictionary<int, SkillInfo>(),
        Buffs             = new Dictionary<int, BuffInfo>(),
        Professions       = new Dictionary<int, ProfessionInfo>(),
        Attributes        = new Dictionary<int, AttributeInfo>(),
        AttributeProfiles = new Dictionary<int, AttributeProfileInfo>(),
        Items             = new Dictionary<int, ItemInfo>(),
    };

    private static object EmptyCacheFor(GameDataTableKind kind) => kind switch
    {
        GameDataTableKind.Skill       => new Dictionary<int, SkillInfo>(),
        GameDataTableKind.Buff        => new Dictionary<int, BuffInfo>(),
        GameDataTableKind.Profession  => new Dictionary<int, ProfessionInfo>(),
        GameDataTableKind.Talent      => new Dictionary<int, TalentInfo>(),
        GameDataTableKind.Attribute   => new Dictionary<int, AttributeInfo>(),
        GameDataTableKind.DamageAttr  => new Dictionary<int, DamageAttrInfo>(),
        GameDataTableKind.Item        => new Dictionary<int, ItemInfo>(),
        GameDataTableKind.Equip       => new Dictionary<int, EquipInfo>(),
        GameDataTableKind.Weapon      => new Dictionary<int, WeaponInfo>(),
        GameDataTableKind.Monster     => new Dictionary<int, MonsterInfo>(),
        GameDataTableKind.Npc         => new Dictionary<int, NpcInfo>(),
        GameDataTableKind.Scene       => new Dictionary<int, SceneInfo>(),
        GameDataTableKind.Map         => new Dictionary<int, MapInfo>(),
        GameDataTableKind.Quest       => new Dictionary<int, QuestInfo>(),
        GameDataTableKind.Dungeon     => new Dictionary<int, DungeonInfo>(),
        GameDataTableKind.Activity    => new Dictionary<int, ActivityInfo>(),
        GameDataTableKind.Achievement => new Dictionary<int, AchievementInfo>(),
        GameDataTableKind.Title       => new Dictionary<int, TitleInfo>(),
        GameDataTableKind.Award       => new Dictionary<int, AwardInfo>(),
        GameDataTableKind.EquipRow     => new Dictionary<int, EquipRowInfo>(),
        GameDataTableKind.EquipAttrLib => new Dictionary<int, EquipAttrLibRowData>(),
        GameDataTableKind.EquipSchoolAttrLib => new Dictionary<int, EquipAttrSchoolLibRowData>(),
        GameDataTableKind.EquipEnchantItem => new Dictionary<int, EnchantItemRowData>(),
        GameDataTableKind.EquipPart    => new Dictionary<int, string>(),
        _ => throw new System.NotSupportedException(),
    };
}
