using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class GameDataWorldService : IGameDataWorld
{
    private IReadOnlyDictionary<int, MonsterInfo>? _monsters;
    private IReadOnlyDictionary<int, NpcInfo>?     _npcs;
    private IReadOnlyDictionary<int, SceneInfo>?   _scenes;
    private IReadOnlyDictionary<int, MapInfo>?     _maps;

    public MonsterInfo? GetMonster(int id) => TryGet(Volatile.Read(ref _monsters), id);
    public NpcInfo?     GetNpc(int id)     => TryGet(Volatile.Read(ref _npcs), id);
    public SceneInfo?   GetScene(int id)   => TryGet(Volatile.Read(ref _scenes), id);
    public MapInfo?     GetMap(int id)     => TryGet(Volatile.Read(ref _maps), id);

    internal void LoadMonsters(IReadOnlyDictionary<int, MonsterInfo> cache)
        => Volatile.Write(ref _monsters, cache);
    internal void LoadNpcs(IReadOnlyDictionary<int, NpcInfo> cache)
        => Volatile.Write(ref _npcs, cache);
    internal void LoadScenes(IReadOnlyDictionary<int, SceneInfo> cache)
        => Volatile.Write(ref _scenes, cache);
    internal void LoadMaps(IReadOnlyDictionary<int, MapInfo> cache)
        => Volatile.Write(ref _maps, cache);

    private static T? TryGet<T>(IReadOnlyDictionary<int, T>? cache, int id) where T : struct
    {
        if (cache is null) return null;
        return cache.TryGetValue(id, out var info) ? info : null;
    }
}
