using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Deep-Slumber Psychoscope (season cultivate) reflection walk for
/// <see cref="PandaInventoryPullReader"/> (Task F5). Walks the live
/// <c>CharSerialize</c> instance the same collaborator already resolves for
/// inventory/loadout, following the container chain recon'd from the game's
/// generated code (Cpp2IL, Panda.ZRpcGen):
/// <c>CharSerialize.SeasonCultivateLineData</c> → map keyed by <c>lineId</c> →
/// <c>CultivateLineData</c> → map keyed by <c>subType</c> →
/// <c>CultivateLineSubTypeData</c> → map keyed by <c>areaId</c> →
/// <c>CultivateAreaData</c> (<c>IsActive</c>, <c>ActivateEffectScore</c>, plus
/// three node maps: big → <c>FantasyId</c>, middle → <c>ItemId</c>, normal →
/// <c>ActiveLevel</c>); and <c>CharSerialize.SeasonRoleLevelData</c> → map
/// keyed by <c>seasonId</c> → object with <c>Level</c>.
///
/// <para>Every generated map property is resolved via <see cref="MapProp"/>,
/// which prefers the <c>&lt;Name&gt;__Value</c> shape over the bare name (both
/// have been observed on other proto-generated maps in this codebase — see
/// <see cref="PandaInventoryPullReader.FindMapLikeProperty"/>). Property
/// handles are resolved lazily off the first encountered runtime instance and
/// cached — the same pattern <c>ReadLiveEquipped</c> uses for
/// <c>_equipProperty</c> — since every CultivateAreaData (and each node's
/// value type) shares one runtime type.</para>
///
/// <para><b>Passive.</b> Reads <see cref="PandaInventoryPullReader.TryGetLiveCharSerialize"/>
/// only — never drives <c>EnsureResolved</c>. Every reflection step is
/// nil-safe (<see cref="SafeGet"/> / try-catch); a missing property, an
/// unresolved map, or a structural surprise yields an empty/false result for
/// that branch rather than throwing, so a partial season-cultivate shape still
/// surfaces whatever was readable.</para>
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    // CharSerialize.SeasonCultivateLineData -> container object.
    private PropertyInfo? _seasonCultivateLineDataProperty;
    // SeasonCultivateLineData.SeasonCultivateLineMap(__Value) -> map<lineId, CultivateLineData>.
    private PropertyInfo? _seasonCultivateLineMapProperty;
    // CultivateLineData.CultivateLineMap(__Value) -> map<subType, CultivateLineSubTypeData>.
    private PropertyInfo? _cultivateLineMapProperty;
    // CultivateLineSubTypeData.CultivateLineDataMap(__Value) -> map<areaId, CultivateAreaData>.
    private PropertyInfo? _cultivateLineDataMapProperty;
    // CultivateAreaData.IsActive -> bool / ActivateEffectScore -> long.
    private PropertyInfo? _cultivateAreaIsActiveProperty;
    private PropertyInfo? _cultivateAreaScoreProperty;

    // CharSerialize.SeasonRoleLevelData -> container object.
    private PropertyInfo? _seasonRoleLevelDataProperty;
    // SeasonRoleLevelData.SeasonRoleLevelMap(__Value) -> map<seasonId, object{Level}>.
    private PropertyInfo? _seasonRoleLevelMapProperty;
    // The season-level entry's Level property (single runtime type, cached on first hit).
    private PropertyInfo? _seasonRoleLevelValueProperty;

    // Per-(runtime type, member name) cache for the three node maps (big/middle/normal) and their
    // value-type property (FantasyId/ItemId/ActiveLevel). Node value objects are a DIFFERENT runtime
    // type per node kind, so a single ??= field per kind (as used above) isn't enough — this small
    // cache keeps ReadNodePairs a flat, allocation-light lookup across repeated calls.
    private readonly Dictionary<(Type Type, string Name), PropertyInfo?> _deepSlumberPropCache = new();

    /// <summary>
    /// Reads the local player's full live Deep-Slumber Psychoscope state, or <c>null</c> when the live
    /// <c>CharSerialize</c> container hasn't resolved yet. Passive — never drives resolution.
    /// </summary>
    internal DeepSlumberState? ReadDeepSlumber()
    {
        var charSerialize = TryGetLiveCharSerialize();
        if (charSerialize is null) return null;

        var state = new DeepSlumberState(ReadSeasonLevels(charSerialize), ReadCultivateLines(charSerialize));
        OnDeepSlumberReadLogged(state);
        return state;
    }

    // CharSerialize.SeasonCultivateLineData -> every (lineId, subType) variant's areas.
    private List<DeepSlumberLine> ReadCultivateLines(object charSerialize)
    {
        var lines = new List<DeepSlumberLine>();
        _seasonCultivateLineDataProperty ??= charSerialize.GetType().GetProperty("SeasonCultivateLineData", AnyInstance);
        object? container = SafeGet(_seasonCultivateLineDataProperty, charSerialize);
        if (container is null) return lines;

        _seasonCultivateLineMapProperty ??= MapProp(container.GetType(), "SeasonCultivateLineMap");
        object? lineMap = SafeGet(_seasonCultivateLineMapProperty, container);
        if (lineMap is null) return lines;

        foreach (var (lineKey, lineObj) in EnumerateMapEntries(lineMap))
        {
            if (lineObj is null) continue;
            lines.AddRange(ReadLineSubTypes(lineObj, AsInt32(lineKey)));
        }
        return lines;
    }

    // CultivateLineData.CultivateLineMap -> one DeepSlumberLine per subType.
    private List<DeepSlumberLine> ReadLineSubTypes(object lineObj, int lineId)
    {
        var result = new List<DeepSlumberLine>();
        _cultivateLineMapProperty ??= MapProp(lineObj.GetType(), "CultivateLineMap");
        object? subTypeMap = SafeGet(_cultivateLineMapProperty, lineObj);
        if (subTypeMap is null) return result;

        foreach (var (subTypeKey, subTypeObj) in EnumerateMapEntries(subTypeMap))
        {
            if (subTypeObj is null) continue;
            result.Add(new DeepSlumberLine(lineId, AsInt32(subTypeKey), ReadAreas(subTypeObj)));
        }
        return result;
    }

    // CultivateLineSubTypeData.CultivateLineDataMap -> each area's full node/activation state.
    private List<DeepSlumberArea> ReadAreas(object subTypeObj)
    {
        var areas = new List<DeepSlumberArea>();
        _cultivateLineDataMapProperty ??= MapProp(subTypeObj.GetType(), "CultivateLineDataMap");
        object? areaMap = SafeGet(_cultivateLineDataMapProperty, subTypeObj);
        if (areaMap is null) return areas;

        foreach (var (areaKey, areaObj) in EnumerateMapEntries(areaMap))
        {
            if (areaObj is null) continue;
            areas.Add(ReadArea(areaObj, AsInt32(areaKey)));
        }
        return areas;
    }

    // One CultivateAreaData -> IsActive / ActivateEffectScore + the three node-pair lists.
    private DeepSlumberArea ReadArea(object areaObj, int areaId)
    {
        var areaType = areaObj.GetType();
        _cultivateAreaIsActiveProperty ??= areaType.GetProperty("IsActive", AnyInstance);
        _cultivateAreaScoreProperty ??= areaType.GetProperty("ActivateEffectScore", AnyInstance);

        bool isActive = TryReadBool(areaObj, _cultivateAreaIsActiveProperty);
        long score = TryReadInt64(areaObj, _cultivateAreaScoreProperty);

        var big = ReadNodePairs(areaObj, "CultivateBigNodeMap", "FantasyId");
        var middle = ReadNodePairs(areaObj, "CultivateMiddleNodeMap", "ItemId");
        var normal = ReadNodePairs(areaObj, "CultivateNormalNodeMap", "ActiveLevel");
        return new DeepSlumberArea(areaId, isActive, score, big, middle, normal);
    }

    // Reads one node map (big/middle/normal) into [nodeId, value] pairs. The map property lives on
    // areaObj's type; the value property lives on each node entry's (kind-specific) type — both
    // resolved once per (type, name) via the small cache above.
    private List<int[]> ReadNodePairs(object areaObj, string mapName, string valuePropertyName)
    {
        var pairs = new List<int[]>();
        var mapProperty = CachedMapProp(areaObj.GetType(), mapName);
        object? map = SafeGet(mapProperty, areaObj);
        if (map is null) return pairs;

        foreach (var (nodeKey, nodeObj) in EnumerateMapEntries(map))
        {
            if (nodeObj is null) continue;
            var valueProperty = CachedProp(nodeObj.GetType(), valuePropertyName);
            pairs.Add(new[] { AsInt32(nodeKey), TryReadInt32(nodeObj, valueProperty) });
        }
        return pairs;
    }

    // CharSerialize.SeasonRoleLevelData -> [seasonId, Level] pairs.
    private List<int[]> ReadSeasonLevels(object charSerialize)
    {
        var levels = new List<int[]>();
        _seasonRoleLevelDataProperty ??= charSerialize.GetType().GetProperty("SeasonRoleLevelData", AnyInstance);
        object? container = SafeGet(_seasonRoleLevelDataProperty, charSerialize);
        if (container is null) return levels;

        _seasonRoleLevelMapProperty ??= MapProp(container.GetType(), "SeasonRoleLevelMap");
        object? map = SafeGet(_seasonRoleLevelMapProperty, container);
        if (map is null) return levels;

        foreach (var (seasonKey, levelObj) in EnumerateMapEntries(map))
        {
            if (levelObj is null) continue;
            _seasonRoleLevelValueProperty ??= levelObj.GetType().GetProperty("Level", AnyInstance);
            levels.Add(new[] { AsInt32(seasonKey), TryReadInt32(levelObj, _seasonRoleLevelValueProperty) });
        }
        return levels;
    }

    private PropertyInfo? CachedMapProp(Type type, string name)
    {
        var key = (type, name);
        if (_deepSlumberPropCache.TryGetValue(key, out var cached)) return cached;
        var prop = MapProp(type, name);
        _deepSlumberPropCache[key] = prop;
        return prop;
    }

    private PropertyInfo? CachedProp(Type type, string name)
    {
        var key = (type, name);
        if (_deepSlumberPropCache.TryGetValue(key, out var cached)) return cached;
        var prop = type.GetProperty(name, AnyInstance);
        _deepSlumberPropCache[key] = prop;
        return prop;
    }

    // Resolves a generated map-like property, preferring the "<Name>__Value" shape over the bare name
    // (the ordering the F5 recon pinned for the season-cultivate containers specifically).
    private static PropertyInfo? MapProp(Type t, string name)
        => t.GetProperty(name + "__Value", AnyInstance) ?? t.GetProperty(name, AnyInstance);

    private static bool TryReadBool(object source, PropertyInfo? prop)
    {
        if (prop is null) return false;
        try { return prop.GetValue(source) is true; }
        catch { return false; }
    }
}
