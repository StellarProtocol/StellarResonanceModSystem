using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public sealed class GameDataWorldServiceTests
{
    [Fact]
    public void GetMonster_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataWorldService();
        svc.LoadMonsters(new Dictionary<int, MonsterInfo>
        {
            [1001] = new MonsterInfo(1001, "Frost Wraith", 45, 3, "icons/1001.png"),
        });

        Assert.Equal(45, svc.GetMonster(1001)!.Value.Level);
    }

    [Fact]
    public void GetNpc_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataWorldService();
        svc.LoadNpcs(new Dictionary<int, NpcInfo>
        {
            [200] = new NpcInfo(200, "Innkeeper Vela", "Inn", 1),
        });

        Assert.Equal("Inn", svc.GetNpc(200)!.Value.Title);
    }

    [Fact]
    public void GetScene_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataWorldService();
        svc.LoadScenes(new Dictionary<int, SceneInfo>
        {
            [8] = new SceneInfo(8, "Brentwood Town", 1, 0),
        });

        Assert.Equal("Brentwood Town", svc.GetScene(8)!.Value.Name);
    }

    [Fact]
    public void GetMap_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataWorldService();
        svc.LoadMaps(new Dictionary<int, MapInfo>
        {
            [1] = new MapInfo(1, "Brentwood", "Starting area", "icons/map1.png"),
        });

        Assert.Equal("Brentwood", svc.GetMap(1)!.Value.Name);
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknownIds()
    {
        var svc = new GameDataWorldService();
        Assert.Null(svc.GetMonster(0));
        Assert.Null(svc.GetNpc(0));
        Assert.Null(svc.GetScene(0));
        Assert.Null(svc.GetMap(0));
    }
}
