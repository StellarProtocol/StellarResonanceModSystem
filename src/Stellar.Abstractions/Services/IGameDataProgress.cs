using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Abstractions.Services;

/// <summary>Static-data lookups for progression-related rows.</summary>
public interface IGameDataProgress
{
    /// <summary>Returns the quest row for <paramref name="id"/>, or null if unknown.</summary>
    QuestInfo? GetQuest(int id);

    /// <summary>Returns the dungeon row for <paramref name="id"/>, or null if unknown.</summary>
    DungeonInfo? GetDungeon(int id);

    /// <summary>Returns the activity row for <paramref name="id"/>, or null if unknown.</summary>
    ActivityInfo? GetActivity(int id);

    /// <summary>Returns the achievement row for <paramref name="id"/>, or null if unknown.</summary>
    AchievementInfo? GetAchievement(int id);

    /// <summary>Returns the title row for <paramref name="id"/>, or null if unknown.</summary>
    TitleInfo? GetTitle(int id);

    /// <summary>Returns the award row for <paramref name="id"/>, or null if unknown.</summary>
    AwardInfo? GetAward(int id);
}
