using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Abstractions.Services;

/// <summary>Static-data lookups for world-related rows.</summary>
public interface IGameDataWorld
{
    /// <summary>Returns the monster row for <paramref name="id"/>, or null if unknown.</summary>
    MonsterInfo? GetMonster(int id);

    /// <summary>
    /// Resolves the <see cref="MonsterInfo"/> for a live entity by reading the entity's
    /// config/template id from the cached combat-wire attribute (attr id 10 =
    /// <c>AttrTypeIds.AttrId</c>), then looking up the monster table. Returns <c>null</c>
    /// when the entity has no cached attr-10 value, when the config id is absent from the
    /// monster table, or when the monster table has not yet been loaded.
    /// </summary>
    /// <param name="entityId">The live entity id (uuid-bearing).</param>
    MonsterInfo? GetMonsterByEntity(EntityId entityId);

    /// <summary>Returns the NPC row for <paramref name="id"/>, or null if unknown.</summary>
    NpcInfo? GetNpc(int id);

    /// <summary>Returns the scene row for <paramref name="id"/>, or null if unknown.</summary>
    SceneInfo? GetScene(int id);

    /// <summary>Returns the map row for <paramref name="id"/>, or null if unknown.</summary>
    MapInfo? GetMap(int id);
}
