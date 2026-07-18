using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    // Deferred World-domain projections — Monster, Npc, Scene, Map.

    // ===== Monster ========================================================

    /// <summary><c>Bokura.MonsterTableBase</c>.</summary>
    private IReadOnlyDictionary<int, MonsterInfo> LoadMonsters()
    {
        return LoadDeferredTable<MonsterInfo>(
            label: "Monster",
            typeName: "Bokura.MonsterTableBase",
            capacityHint: 2048,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                var level = ReadInt(row, rowType, "Level");
                var factionId = ReadInt(row, rowType, "FactionId");
                if (factionId == 0)
                {
                    factionId = ReadInt(row, rowType, "Faction");
                }
                var iconPath = ReadString(row, rowType, "IconPath");
                var monsterType = ReadInt(row, rowType, "MonsterType");

                return (id, new MonsterInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Level: level,
                    FactionId: factionId,
                    IconPath: iconPath ?? string.Empty)
                {
                    MonsterType = monsterType,
                    IsBoss = MonsterBossRule.IsBoss(monsterType)
                });
            });
    }

    // ===== Npc ============================================================

    /// <summary><c>Bokura.NpcTableBase</c>.</summary>
    private IReadOnlyDictionary<int, NpcInfo> LoadNpcs()
    {
        return LoadDeferredTable<NpcInfo>(
            label: "Npc",
            typeName: "Bokura.NpcTableBase",
            capacityHint: 1024,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                var title = ReadStringOrMlString(row, rowType, "Title");
                var factionId = ReadInt(row, rowType, "FactionId");
                if (factionId == 0)
                {
                    factionId = ReadInt(row, rowType, "Faction");
                }

                return (id, new NpcInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Title: title ?? string.Empty,
                    FactionId: factionId));
            });
    }

    // ===== Scene ==========================================================

    /// <summary><c>Bokura.SceneTableBase</c>.</summary>
    private IReadOnlyDictionary<int, SceneInfo> LoadScenes()
    {
        return LoadDeferredTable<SceneInfo>(
            label: "Scene",
            typeName: "Bokura.SceneTableBase",
            capacityHint: 256,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                var mapId = ReadInt(row, rowType, "MapId");
                var sceneKind = ReadInt(row, rowType, "SceneType");

                return (id, new SceneInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    MapId: mapId,
                    SceneKind: sceneKind));
            });
    }

    // ===== Map ============================================================

    /// <summary><c>Bokura.MapInfoTableBase</c>.</summary>
    private IReadOnlyDictionary<int, MapInfo> LoadMaps()
    {
        return LoadDeferredTable<MapInfo>(
            label: "Map",
            typeName: "Bokura.MapInfoTableBase",
            capacityHint: 128,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                var desc = ReadStringOrMlString(row, rowType, "Description");
                if (string.IsNullOrEmpty(desc))
                {
                    desc = ReadStringOrMlString(row, rowType, "Desc");
                }
                var iconPath = ReadString(row, rowType, "IconPath");

                return (id, new MapInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Description: desc ?? string.Empty,
                    IconPath: iconPath ?? string.Empty));
            });
    }
}
