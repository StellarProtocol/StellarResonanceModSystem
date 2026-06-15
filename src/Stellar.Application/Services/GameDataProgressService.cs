using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class GameDataProgressService : IGameDataProgress
{
    private IReadOnlyDictionary<int, QuestInfo>?       _quests;
    private IReadOnlyDictionary<int, DungeonInfo>?     _dungeons;
    private IReadOnlyDictionary<int, ActivityInfo>?    _activities;
    private IReadOnlyDictionary<int, AchievementInfo>? _achievements;
    private IReadOnlyDictionary<int, TitleInfo>?       _titles;
    private IReadOnlyDictionary<int, AwardInfo>?       _awards;

    public QuestInfo?       GetQuest(int id)       => TryGet(Volatile.Read(ref _quests), id);
    public DungeonInfo?     GetDungeon(int id)     => TryGet(Volatile.Read(ref _dungeons), id);
    public ActivityInfo?    GetActivity(int id)    => TryGet(Volatile.Read(ref _activities), id);
    public AchievementInfo? GetAchievement(int id) => TryGet(Volatile.Read(ref _achievements), id);
    public TitleInfo?       GetTitle(int id)       => TryGet(Volatile.Read(ref _titles), id);
    public AwardInfo?       GetAward(int id)       => TryGet(Volatile.Read(ref _awards), id);

    internal void LoadQuests(IReadOnlyDictionary<int, QuestInfo> cache)
        => Volatile.Write(ref _quests, cache);
    internal void LoadDungeons(IReadOnlyDictionary<int, DungeonInfo> cache)
        => Volatile.Write(ref _dungeons, cache);
    internal void LoadActivities(IReadOnlyDictionary<int, ActivityInfo> cache)
        => Volatile.Write(ref _activities, cache);
    internal void LoadAchievements(IReadOnlyDictionary<int, AchievementInfo> cache)
        => Volatile.Write(ref _achievements, cache);
    internal void LoadTitles(IReadOnlyDictionary<int, TitleInfo> cache)
        => Volatile.Write(ref _titles, cache);
    internal void LoadAwards(IReadOnlyDictionary<int, AwardInfo> cache)
        => Volatile.Write(ref _awards, cache);

    private static T? TryGet<T>(IReadOnlyDictionary<int, T>? cache, int id) where T : struct
    {
        if (cache is null) return null;
        return cache.TryGetValue(id, out var info) ? info : null;
    }
}
