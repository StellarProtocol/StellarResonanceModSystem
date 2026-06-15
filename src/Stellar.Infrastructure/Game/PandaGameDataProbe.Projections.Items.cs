using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    // ===== FightAttr icon map =============================================

    // base FightAttr id (EAttrType rounded to its ×10 base) -> game icon path.
    // AttrDescriptionBase carries no icon; FightAttrTableBase does (column "Icon",
    // e.g. "ui/atlas/weaponhero/new/common_icon05"). Built before LoadAttributes
    // so AttributeInfo.IconPath can be populated by base lookup.
    private IReadOnlyDictionary<int, string> _attrIconByBase = new Dictionary<int, string>();

    // base FightAttr id (×10) -> AttrNumType (0 = raw int, >=1 = percent value/100).
    // Authoritative value-format classifier; consumed by AttributeInfo.NumType so the
    // StatInspector formats %/raw from game data instead of a name heuristic.
    private IReadOnlyDictionary<int, int> _attrNumTypeByBase = new Dictionary<int, int>();

    private readonly record struct AttrIconRow(string Path, int NumType);   // LoadEagerTable needs a struct TInfo

    private IReadOnlyDictionary<int, string> LoadFightAttrIcons()
    {
        var rows = LoadEagerTable<AttrIconRow>(
            label: "FightAttrIcon",
            typeName: "Bokura.FightAttrTableBase",
            capacityHint: 192,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);
                var icon = ReadString(row, rowType, "Icon") ?? string.Empty;
                var numType = ReadInt(row, rowType, "AttrNumType");
                return (id, new AttrIconRow(icon, numType));
            });
        var iconMap = new Dictionary<int, string>(rows.Count);
        var numMap  = new Dictionary<int, int>(rows.Count);
        foreach (var kv in rows)
        {
            if (!string.IsNullOrEmpty(kv.Value.Path)) iconMap[kv.Key] = kv.Value.Path;
            numMap[kv.Key] = kv.Value.NumType;
        }
        _attrNumTypeByBase = numMap;
        return iconMap;
    }

    // ===== Attribute ======================================================

    /// <summary>
    /// Build the Attribute lookup from <c>Bokura.AttrDescriptionBase</c>. Recon
    /// (Phase 5 polish) showed <c>BasicAttrTableBase</c> has Count=1 with only
    /// numeric grow columns (no Name / ShortName / IconPath / Category), so the
    /// original "BasicAttr is primary + AttrDescription joins Category" plan
    /// was structurally impossible on this build. <c>AttrDescriptionBase</c> is
    /// the only attribute-related table with a user-facing string column —
    /// schema: <c>Id, Description</c>. Both <see cref="AttributeInfo.Name"/>
    /// and <see cref="AttributeInfo.ShortName"/> project from <c>Description</c>
    /// (no separate short-form on this build). <see cref="AttributeInfo.Group"/>
    /// stays <see cref="AttributeGroup.Unknown"/> until a category-keyed table
    /// surfaces in Phase 6 (<c>StatInspector</c> consumes it for HUD labels).
    /// </summary>
    private IReadOnlyDictionary<int, AttributeInfo> LoadAttributes()
    {
        var firstLogged = false;

        return LoadEagerTable<AttributeInfo>(
            label: "Attribute",
            typeName: "Bokura.AttrDescriptionBase",
            capacityHint: 1536,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var description = ReadStringOrMlString(row, rowType, "Description");

                if (!firstLogged)
                {
                    firstLogged = true;
                    LogFirstAttributeRow(id, description, description, string.Empty, 0);
                }

                // Icon: AttrDescriptionBase has none; join FightAttrTableBase by
                // the ×10 base id (EAttrType 11011 "AttrStrengthTotal" -> 11010
                // "AttrStrength"). Total/Add/Per variants share the base icon.
                var iconPath = _attrIconByBase.TryGetValue(id - id % 10, out var ip) ? ip : string.Empty;

                var numType = _attrNumTypeByBase.TryGetValue(id - id % 10, out var nt) ? nt : -1;

                return (id, new AttributeInfo(
                    Id: id,
                    Name: description ?? string.Empty,
                    ShortName: description ?? string.Empty,
                    IconPath: iconPath,
                    Group: AttributeGroup.Unknown)
                { NumType = numType });
            });
    }

    // ===== AttributeProfile ===============================================

    /// <summary>
    /// Build the AttributeProfile lookup from <c>Bokura.ProfileAttrTableBase</c>.
    /// Each row classifies an attribute into a UI panel group:
    /// <c>Id, AttrId, Name, Type (Int32), TypeDisplayName (MLString)</c>.
    /// Phase 6 Iter 1 — verify the Type code mapping via the first-row
    /// diagnostic dump (1=Offensive / 2=Defensive / 3=Support /
    /// 4=ElementalAttack / 5=ElementalBonus per spec §5; downstream picker
    /// uses <see cref="AttributeProfileInfo.TypeDisplayName"/> verbatim so
    /// the mapping is observational, not load-bearing.
    /// </summary>
    private IReadOnlyDictionary<int, AttributeProfileInfo> LoadAttributeProfiles()
    {
        var firstLogged = false;

        return LoadEagerTable<AttributeProfileInfo>(
            label: "AttributeProfile",
            typeName: "Bokura.ProfileAttrTableBase",
            capacityHint: 64,
            projector: (row, rowType) =>
            {
                var rowId = ReadInt(row, rowType, "Id");
                if (rowId == 0) return (0, default);

                var attrId          = ReadInt(row, rowType, "AttrId");
                var name            = ReadStringOrMlString(row, rowType, "Name");
                var type            = ReadInt(row, rowType, "Type");
                var typeDisplayName = ReadStringOrMlString(row, rowType, "TypeDisplayName");

                if (!firstLogged)
                {
                    firstLogged = true;
                    LogFirstAttributeProfileRow(rowId, attrId, name, type, typeDisplayName);
                }

                // Key the cache by EAttrType `attrId` (e.g. AttrMaxHp=11320) — NOT
                // by the table's `Id` column (1..88). Plugins look up via EAttrType
                // codes; the row Id is an internal table index nothing surfaces.
                // GameDataCombatService.GetAttributeProfile handles the Total→Base
                // (attrId+1 → attrId) fallback so callers can lookup with either
                // the Base or Total variant.
                if (attrId == 0) return (0, default);
                return (attrId, new AttributeProfileInfo(
                    AttrId: attrId,
                    Name: name ?? string.Empty,
                    Type: type,
                    TypeDisplayName: typeDisplayName ?? string.Empty));
            });
    }

    // ===== Item ===========================================================

    /// <summary>
    /// Build the Item lookup from <c>Bokura.ItemTableBase</c>. Recon confirmed
    /// (Iter 2): the row exposes <c>Id, Name, Description, IconPath,
    /// Quality (Int32), Type (Int32), GroupId (Int32)</c>. The 5_500_000 –
    /// 5_509_999 Id range is special-cased to <see cref="ItemKind.Module"/> per
    /// spec §4; <see cref="LogItemModuleRangeDistribution"/> post-load dumps the
    /// Type distribution inside that range so the bounds + heuristic can be
    /// validated against live data in Phase 7.
    /// </summary>
    private IReadOnlyDictionary<int, ItemInfo> LoadItems()
    {
        var firstLogged = false;
        var moduleRangeStats = new ModuleRangeStats();

        var result = LoadEagerTable<ItemInfo>(
            label: "Item",
            typeName: "Bokura.ItemTableBase",
            capacityHint: 4096,
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
                // ItemTableBase column is `Icon` (not `IconPath`).
                var iconPath = ReadString(row, rowType, "Icon");
                if (string.IsNullOrEmpty(iconPath))
                {
                    iconPath = ReadString(row, rowType, "IconPath");
                }
                var quality = ReadInt(row, rowType, "Quality");
                var typeInt = ReadInt(row, rowType, "Type");
                var groupId = ReadInt(row, rowType, "GroupId");

                if (!firstLogged)
                {
                    firstLogged = true;
                    LogFirstItemRow(new ItemRowDiagInfo(id, name, desc, iconPath, quality, typeInt, groupId));
                }
                moduleRangeStats.Observe(id, typeInt);

                return (id, new ItemInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Description: desc ?? string.Empty,
                    IconPath: iconPath ?? string.Empty,
                    Quality: quality,
                    Kind: MapItemKind(typeInt, id),
                    GroupId: groupId));
            });

        LogItemModuleRangeDistribution(moduleRangeStats);
        return result;
    }

    /// <summary>
    /// Per-Type accumulator used by <see cref="LoadItems"/> to surface the
    /// <see cref="ItemKind.Module"/> Id-range distribution under
    /// <c>STELLAR_DIAGNOSTICS=1</c>.
    /// </summary>
    private sealed class ModuleRangeStats
    {
        public int InRangeCount;
        public readonly Dictionary<int, int> InRangeTypeCounts = new();
        public readonly Dictionary<int, int> OutOfRangeTypeCounts = new();

        public void Observe(int id, int typeInt)
        {
            if (id >= 5_500_000 && id < 5_510_000)
            {
                InRangeCount++;
                InRangeTypeCounts[typeInt] = InRangeTypeCounts.TryGetValue(typeInt, out var c) ? c + 1 : 1;
            }
            else
            {
                OutOfRangeTypeCounts[typeInt] = OutOfRangeTypeCounts.TryGetValue(typeInt, out var c) ? c + 1 : 1;
            }
        }
    }
}
