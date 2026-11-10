using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// uuid → item lookups for the per-class loadout resolver and the live-equipped read
/// (<see cref="PandaInventoryPullReader"/>). Split out of <c>PerClassLoadout.cs</c> (2026-11-10).
///
/// <para><b>Why keyed, not walked (raid 2026-11-10, <c>fw:lo.perClass</c> avg 20 ms / max 83 ms, 50× in
/// one raid):</b> the resolve used to index EVERY item in EVERY package (and materialize EVERY owned
/// module's <c>ModInfo</c>) to look up the ~20-70 uuids the loadouts actually reference. A hoarder's
/// inventory is thousands of items at ~4 reflective IL2CPP calls each, and the resolve re-runs on every
/// CharSerialize merge (field-agnostic by owner ruling — in combat the server drips one every ~5 s), so
/// the full walk was a recurring 40-80 ms frame. The maps are protobuf <c>MapField&lt;long, T&gt;</c>
/// keyed by uuid, so each wanted uuid is now a <c>ContainsKey</c> (+ <c>get_Item</c> on a hit) per
/// package: cost scales with what the loadouts reference, not with the bag. A map type that lacks
/// either member falls back to the old full walk (filtered to the wanted set), so nothing is lost.</para>
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    // ContainsKey(long) / get_Item(long) per map runtime type — resolved once (misses cached too).
    private static readonly ConcurrentDictionary<Type, (MethodInfo? Contains, MethodInfo? Get)> LongKeyMembersByType = new();
    private static readonly Type[] LongKeyParam = { typeof(long) };

    // Index only the WANTED uuids across ALL packages (mirrors the game's GetItemTabDataByUuid, which scans
    // itemPackage.packages[*].items[uuid]). A plan's gear/modules may live in any package, and a non-active
    // plan's items stay in the packages (confirmed in-game 2026-08-03). Packages are visited in the same
    // order as before and a later hit overwrites an earlier one — identical to the old whole-bag index.
    private Dictionary<long, object> BuildUuidIndex(object charSerialize, HashSet<long> wanted)
    {
        var index = new Dictionary<long, object>(wanted.Count);
        if (wanted.Count == 0) return index;
        object? itemPackage = SafeGet(_itemPackageProperty, charSerialize);
        if (itemPackage is null) return index;
        object? packagesMap = SafeGet(_packagesProperty, itemPackage);
        if (packagesMap is null) return index;

        foreach (var package in EnumerateMapValues(packagesMap))
        {
            if (package is null) continue;
            _packageItemsProperty ??= FindMapLikeProperty(package.GetType(), "Items");
            object? itemsMap = SafeGet(_packageItemsProperty, package);
            if (itemsMap is null) continue;
            if (!TryIndexByKey(itemsMap, wanted, index)) IndexItemsByWalk(itemsMap, wanted, index);
        }
        return index;
    }

    // The old path, kept as the fallback for a map type without keyed members: walk every item, keep the
    // wanted ones (keyed by the item's own Uuid, as before).
    private void IndexItemsByWalk(object itemsMap, HashSet<long> wanted, Dictionary<long, object> index)
    {
        foreach (var entry in EnumerateMapValues(itemsMap))
        {
            if (entry is null) continue;
            long uuid = TryReadInt64(entry, _itemUuidProperty);
            if (uuid != 0 && wanted.Contains(uuid)) index[uuid] = entry;
        }
    }

    // CharSerialize.Mod.ModInfos restricted to the wanted MODULE uuids — the only keys
    // BuildModuleInfoOrNull ever looks up (ReadInitLinkNums → TryGetValue(uuid)). Null when the map is
    // unresolved (same contract as ReadModInfosByUuid); whole-map materialize when keyed lookup is unavailable.
    private Dictionary<long, object>? ReadModInfosFor(object charSerialize, HashSet<long> moduleUuids)
    {
        var modInfosMap = ReadModInfosMap(charSerialize);
        if (modInfosMap is null) return null;
        var result = new Dictionary<long, object>(moduleUuids.Count);
        return TryIndexByKey(modInfosMap, moduleUuids, result) ? result : MaterializeKeyedDict<long>(modInfosMap);
    }

    // Keyed lookup of every wanted uuid in one MapField<long, T>. False = the type has no usable
    // ContainsKey/get_Item(long), or a call threw — the caller then walks the map instead (re-adding the same
    // values is harmless). Never throws.
    private static bool TryIndexByKey(object map, HashSet<long> wanted, Dictionary<long, object> index)
    {
        var (contains, get) = LongKeyMembersByType.GetOrAdd(map.GetType(), static t => (
            t.GetMethod("ContainsKey", AnyInstance, binder: null, types: LongKeyParam, modifiers: null),
            t.GetMethod("get_Item", AnyInstance, binder: null, types: LongKeyParam, modifiers: null)));
        if (contains is null || get is null) return false;

        var arg = new object[1];
        try
        {
            foreach (var uuid in wanted)
            {
                if (uuid == 0) continue;
                arg[0] = uuid;
                // ContainsKey first: a get_Item miss throws KeyNotFound across the IL2CPP boundary (costly).
                if (contains.Invoke(map, arg) is not true) continue;
                var value = get.Invoke(map, arg);
                if (value is not null) index[uuid] = value;
            }
            return true;
        }
        catch { return false; }
    }

    // Every non-zero uuid the plans (and the live set, appended last by the caller) reference. Module
    // uuids are collected separately: only those need a ModInfo lookup.
    private static HashSet<long> CollectWantedUuids(
        IReadOnlyList<(IReadOnlyDictionary<int, long> Equip, IReadOnlyDictionary<int, long> Mod)> plans,
        out HashSet<long> moduleUuids)
    {
        var all = new HashSet<long>();
        moduleUuids = new HashSet<long>();
        foreach (var (equip, mod) in plans)
        {
            foreach (var uuid in equip.Values) if (uuid != 0) all.Add(uuid);
            foreach (var uuid in mod.Values) if (uuid != 0) { all.Add(uuid); moduleUuids.Add(uuid); }
        }
        return all;
    }
}
