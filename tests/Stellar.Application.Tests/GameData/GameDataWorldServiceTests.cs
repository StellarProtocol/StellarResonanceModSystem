using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Services;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public sealed class GameDataWorldServiceTests
{
    private static GameDataWorldService MakeService() => new(new CombatEntityTracker());

    [Fact]
    public void GetMonster_ReturnsRow_WhenKnown()
    {
        var svc = MakeService();
        svc.LoadMonsters(new Dictionary<int, MonsterInfo>
        {
            [1001] = new MonsterInfo(1001, "Frost Wraith", 45, 3, "icons/1001.png"),
        });

        Assert.Equal(45, svc.GetMonster(1001)!.Value.Level);
    }

    [Fact]
    public void GetNpc_ReturnsRow_WhenKnown()
    {
        var svc = MakeService();
        svc.LoadNpcs(new Dictionary<int, NpcInfo>
        {
            [200] = new NpcInfo(200, "Innkeeper Vela", "Inn", 1),
        });

        Assert.Equal("Inn", svc.GetNpc(200)!.Value.Title);
    }

    [Fact]
    public void GetScene_ReturnsRow_WhenKnown()
    {
        var svc = MakeService();
        svc.LoadScenes(new Dictionary<int, SceneInfo>
        {
            [8] = new SceneInfo(8, "Brentwood Town", 1, 0),
        });

        Assert.Equal("Brentwood Town", svc.GetScene(8)!.Value.Name);
    }

    [Fact]
    public void GetMap_ReturnsRow_WhenKnown()
    {
        var svc = MakeService();
        svc.LoadMaps(new Dictionary<int, MapInfo>
        {
            [1] = new MapInfo(1, "Brentwood", "Starting area", "icons/map1.png"),
        });

        Assert.Equal("Brentwood", svc.GetMap(1)!.Value.Name);
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknownIds()
    {
        var svc = MakeService();
        Assert.Null(svc.GetMonster(0));
        Assert.Null(svc.GetNpc(0));
        Assert.Null(svc.GetScene(0));
        Assert.Null(svc.GetMap(0));
    }

    [Fact]
    public void GetMonsterByEntity_ResolvesViaCachedAttr10()
    {
        var tracker = new CombatEntityTracker();
        var svc = new GameDataWorldService(tracker);
        svc.LoadMonsters(new Dictionary<int, MonsterInfo>
        {
            [33301] = new MonsterInfo(33301, "Ancient Purifier", 50, 1, "") { MonsterType = 2, IsBoss = true },
        });

        var eid = new EntityId(0x_0000_0001_0280_0001L);  // arbitrary uuid
        tracker.SetEntityAttribute(eid, AttrTypeIds.AttrId, 33301L);

        var result = svc.GetMonsterByEntity(eid);

        Assert.NotNull(result);
        Assert.Equal("Ancient Purifier", result!.Value.Name);
        Assert.True(result.Value.IsBoss);
    }

    [Fact]
    public void GetMonsterByEntity_ReturnsNull_WhenAttr10Absent()
    {
        var svc = MakeService();
        svc.LoadMonsters(new Dictionary<int, MonsterInfo>
        {
            [33301] = new MonsterInfo(33301, "Ancient Purifier", 50, 1, "") { MonsterType = 2, IsBoss = true },
        });

        // No attr-10 set for this entity.
        Assert.Null(svc.GetMonsterByEntity(new EntityId(0x_0000_0001_0280_0002L)));
    }

    [Fact]
    public void GetMonsterByEntity_ReturnsNull_WhenConfigIdNotInTable()
    {
        var tracker = new CombatEntityTracker();
        var svc = new GameDataWorldService(tracker);
        // Monster table is empty — config id 99999 won't be found.
        svc.LoadMonsters(new Dictionary<int, MonsterInfo>());

        var eid = new EntityId(0x_0000_0001_0280_0003L);
        tracker.SetEntityAttribute(eid, AttrTypeIds.AttrId, 99999L);

        Assert.Null(svc.GetMonsterByEntity(eid));
    }
}
