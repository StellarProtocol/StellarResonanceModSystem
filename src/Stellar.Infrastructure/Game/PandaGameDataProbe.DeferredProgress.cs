using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    // Deferred Progress-domain projections — Quest, Dungeon, Activity,
    // Achievement, Title, Award. The first three pick the "primary" table
    // among several siblings (spec §4 — confirmed during iteration 1).

    // ===== Quest ==========================================================

    /// <summary>
    /// <c>Bokura.QuestTableBase</c> — primary among 8 quest tables (others are
    /// quest-stage / quest-line tables out of scope).
    /// </summary>
    private IReadOnlyDictionary<int, QuestInfo> LoadQuests()
    {
        var firstLogged = false;

        return LoadDeferredTable<QuestInfo>(
            label: "Quest",
            typeName: "Bokura.QuestTableBase",
            capacityHint: 2048,
            projector: (row, rowType) =>
            {
                if (!firstLogged)
                {
                    firstLogged = true;
                    LogFirstQuestRow(row, rowType);
                }

                // Bokura.QuestTableBase rows don't use the canonical "Id" column —
                // try the common naming variants until a non-zero value appears.
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) id = ReadInt(row, rowType, "QuestId");
                if (id == 0) id = ReadInt(row, rowType, "Key");
                if (id == 0) id = ReadInt(row, rowType, "Bid");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                var desc = ReadStringOrMlString(row, rowType, "Description");
                if (string.IsNullOrEmpty(desc))
                {
                    desc = ReadStringOrMlString(row, rowType, "Desc");
                }
                var questKind = ReadInt(row, rowType, "QuestKind");
                if (questKind == 0)
                {
                    questKind = ReadInt(row, rowType, "Type");
                }

                return (id, new QuestInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Description: desc ?? string.Empty,
                    QuestKind: questKind));
            });
    }

    // ===== Dungeon ========================================================

    /// <summary>
    /// <c>Bokura.DungeonsTableBase</c> (note the trailing 's') — canonical
    /// dungeon-id → row table on this build. Spec referenced
    /// <c>Bokura.DungeonTableBase</c> which does not exist; recon of
    /// Panda.Table.dll picked this as the primary among the 11 Dungeon-related
    /// tables (others are challenge/raid/master variants keyed by DungeonId).
    /// Schema: <c>Id, Name, Content (description), MonsterLv (Int32Array),
    /// SceneID, FunctionID, …</c>. No <c>MinLevel</c> / <c>Difficulty</c> single
    /// columns — first <c>MonsterLv</c> entry serves as MinLevel.
    /// </summary>
    private IReadOnlyDictionary<int, DungeonInfo> LoadDungeons()
    {
        return LoadDeferredTable<DungeonInfo>(
            label: "Dungeon",
            typeName: "Bokura.DungeonsTableBase",
            capacityHint: 256,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                // MinLevel — DungeonsTableBase doesn't have a single column;
                // first entry of MonsterLv (Int32Array) is the closest stand-in.
                var monsterLevels = ReadInt32Array(row, rowType, "MonsterLv");
                var minLevel = monsterLevels.Length > 0 ? monsterLevels[0] : 0;
                // Difficulty — no canonical column on this build. PlayType
                // (1=normal/2=hero/3=master based on observed usage) serves as
                // a coarse difficulty signal.
                var difficulty = ReadInt(row, rowType, "PlayType");

                return (id, new DungeonInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    MinLevel: minLevel,
                    Difficulty: difficulty));
            });
    }

    // ===== Activity =======================================================

    /// <summary>
    /// <c>Bokura.UnionActivityTableBase</c> — richest activity table on this
    /// build (24 columns including <c>Id, Name, ActDes, Time</c>). Spec
    /// referenced <c>Bokura.ActivityTableBase</c> which does not exist; recon
    /// picked Union as primary among 4 activity tables. The build stores Time
    /// as a free-form string (display label, e.g. "Mon-Fri 20:00") rather than
    /// unix ms — Start/EndUnixMs remain 0 until a date-keyed table surfaces.
    /// </summary>
    private IReadOnlyDictionary<int, ActivityInfo> LoadActivities()
    {
        return LoadDeferredTable<ActivityInfo>(
            label: "Activity",
            typeName: "Bokura.UnionActivityTableBase",
            capacityHint: 128,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                // Description — ActDes is UnionActivity's user-facing blurb;
                // fall back to Description / Desc if a future table reshape
                // keeps the same loader.
                var desc = ReadStringOrMlString(row, rowType, "ActDes");
                if (string.IsNullOrEmpty(desc))
                {
                    desc = ReadStringOrMlString(row, rowType, "Description");
                    if (string.IsNullOrEmpty(desc))
                    {
                        desc = ReadStringOrMlString(row, rowType, "Desc");
                    }
                }

                return (id, new ActivityInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Description: desc ?? string.Empty,
                    StartUnixMs: 0L,
                    EndUnixMs: 0L));
            });
    }

    // ===== Achievement ====================================================

    /// <summary><c>Bokura.AchievementTableBase</c>.</summary>
    private IReadOnlyDictionary<int, AchievementInfo> LoadAchievements()
    {
        return LoadDeferredTable<AchievementInfo>(
            label: "Achievement",
            typeName: "Bokura.AchievementTableBase",
            capacityHint: 512,
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
                var points = ReadInt(row, rowType, "Points");
                if (points == 0)
                {
                    points = ReadInt(row, rowType, "Score");
                    if (points == 0)
                    {
                        points = ReadInt(row, rowType, "Value");
                    }
                }

                return (id, new AchievementInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Description: desc ?? string.Empty,
                    Points: points));
            });
    }

    // ===== Title ==========================================================

    /// <summary>
    /// <c>Bokura.DungeonTitleTableBase</c> — the only title-id → name table on
    /// this build. Spec referenced <c>Bokura.TitleTableBase</c> which does not
    /// exist; recon found only <c>DungeonTitleTableBase</c> (dungeon-clear
    /// titles) and <c>QuestInfoTitleTableBase</c> (quest episode headings).
    /// DungeonTitle is the closest fit for the "player title" concept — schema:
    /// <c>Id, Name, Content (description), Weight</c>. ColorRgba has no source
    /// column on this build and stays 0 until a richer title table surfaces.
    /// Lookups for non-dungeon titles (e.g. PvP rank titles) will return null
    /// in v1.
    /// </summary>
    private IReadOnlyDictionary<int, TitleInfo> LoadTitles()
    {
        return LoadDeferredTable<TitleInfo>(
            label: "Title",
            typeName: "Bokura.DungeonTitleTableBase",
            capacityHint: 256,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                // Description — DungeonTitle stores it as Content.
                var desc = ReadStringOrMlString(row, rowType, "Content");
                if (string.IsNullOrEmpty(desc))
                {
                    desc = ReadStringOrMlString(row, rowType, "Description");
                    if (string.IsNullOrEmpty(desc))
                    {
                        desc = ReadStringOrMlString(row, rowType, "Desc");
                    }
                }

                return (id, new TitleInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Description: desc ?? string.Empty,
                    ColorRgba: 0u));
            });
    }

    // ===== Award ==========================================================

    /// <summary><c>Bokura.AwardTableBase</c>.</summary>
    private IReadOnlyDictionary<int, AwardInfo> LoadAwards()
    {
        var firstLogged = false;

        return LoadDeferredTable<AwardInfo>(
            label: "Award",
            typeName: "Bokura.AwardTableBase",
            capacityHint: 256,
            projector: (row, rowType) =>
            {
                if (!firstLogged)
                {
                    firstLogged = true;
                    LogFirstAwardRow(row, rowType);
                }

                // Bokura.AwardTableBase rows don't use the canonical "Id" column —
                // try the common naming variants until a non-zero value appears.
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) id = ReadInt(row, rowType, "AwardId");
                if (id == 0) id = ReadInt(row, rowType, "AwardID");
                if (id == 0) id = ReadInt(row, rowType, "Key");
                if (id == 0) id = ReadInt(row, rowType, "Aid");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                var iconPath = ReadString(row, rowType, "IconPath");
                var quality = ReadInt(row, rowType, "Quality");

                return (id, new AwardInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    IconPath: iconPath ?? string.Empty,
                    Quality: quality));
            });
    }
}
