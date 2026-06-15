using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public sealed class GameDataProgressServiceTests
{
    [Fact]
    public void GetQuest_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataProgressService();
        svc.LoadQuests(new Dictionary<int, QuestInfo>
        {
            [10001] = new QuestInfo(10001, "Tutorial", "Learn the basics", 1),
        });

        Assert.Equal("Tutorial", svc.GetQuest(10001)!.Value.Name);
    }

    [Fact]
    public void GetDungeon_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataProgressService();
        svc.LoadDungeons(new Dictionary<int, DungeonInfo>
        {
            [501] = new DungeonInfo(501, "Frost Keep", 50, 2),
        });

        Assert.Equal(50, svc.GetDungeon(501)!.Value.MinLevel);
    }

    [Fact]
    public void GetActivity_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataProgressService();
        svc.LoadActivities(new Dictionary<int, ActivityInfo>
        {
            [777] = new ActivityInfo(777, "Spring Festival", "Limited event",
                1_700_000_000_000, 1_700_500_000_000),
        });

        Assert.Equal("Spring Festival", svc.GetActivity(777)!.Value.Name);
    }

    [Fact]
    public void GetAchievement_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataProgressService();
        svc.LoadAchievements(new Dictionary<int, AchievementInfo>
        {
            [42] = new AchievementInfo(42, "First Steps", "Complete tutorial", 10),
        });

        Assert.Equal(10, svc.GetAchievement(42)!.Value.Points);
    }

    [Fact]
    public void GetTitle_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataProgressService();
        svc.LoadTitles(new Dictionary<int, TitleInfo>
        {
            [1] = new TitleInfo(1, "Champion", "Top performer", 0xFFD700FFu),
        });

        Assert.Equal(0xFFD700FFu, svc.GetTitle(1)!.Value.ColorRgba);
    }

    [Fact]
    public void GetAward_ReturnsRow_WhenKnown()
    {
        var svc = new GameDataProgressService();
        svc.LoadAwards(new Dictionary<int, AwardInfo>
        {
            [100] = new AwardInfo(100, "Gold Medal", "icons/medal.png", 5),
        });

        Assert.Equal(5, svc.GetAward(100)!.Value.Quality);
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknownIds()
    {
        var svc = new GameDataProgressService();
        Assert.Null(svc.GetQuest(0));
        Assert.Null(svc.GetDungeon(0));
        Assert.Null(svc.GetActivity(0));
        Assert.Null(svc.GetAchievement(0));
        Assert.Null(svc.GetTitle(0));
        Assert.Null(svc.GetAward(0));
    }
}
