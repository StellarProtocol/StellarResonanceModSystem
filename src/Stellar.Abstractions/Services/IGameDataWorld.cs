using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Abstractions.Services;

/// <summary>Static-data lookups for world-related rows.</summary>
public interface IGameDataWorld
{
    /// <summary>Returns the monster row for <paramref name="id"/>, or null if unknown.</summary>
    MonsterInfo? GetMonster(int id);

    /// <summary>Returns the NPC row for <paramref name="id"/>, or null if unknown.</summary>
    NpcInfo? GetNpc(int id);

    /// <summary>Returns the scene row for <paramref name="id"/>, or null if unknown.</summary>
    SceneInfo? GetScene(int id);

    /// <summary>Returns the map row for <paramref name="id"/>, or null if unknown.</summary>
    MapInfo? GetMap(int id);
}
