using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Per-class loadout resolution for <see cref="PandaInventoryPullReader"/> (per-class gear/modules RE,
/// 2026-08-03). A class swap NEVER re-broadcasts gear/modules ([ClassGearDiag]-proven), so the live
/// <c>GetSelfGear</c>/<c>GetModules</c> are class-blind. The per-class data IS client-side: the loadout
/// probe reads each saved plan's <c>equipInfoMap</c>/<c>modInfoMap</c> (slot → item uuid) via the Lua
/// bridge, and this resolver turns those uuids into full <see cref="GearInstance"/>/<see cref="ModuleInfo"/>
/// by looking each uuid up in <c>CharSerialize.itemPackage</c> — the same container the module inventory
/// already walks, and the same scan the game's own <c>items_vm.GetItemTabDataByUuid</c> uses. Confirmed
/// in-game 2026-08-03: a NON-active plan's gear + module uuids resolve here with full rolls/parts
/// (<c>recon/loadout-switch-findings.md</c> § Phase 0).
///
/// <para>Item-intrinsic detail (config, quality, rolls, perfection, breakthrough, module parts) comes
/// from the item. REFINE + ENCHANT live in the equip-slot view (<c>CharSerialize.equip.equipList</c>),
/// which is current-class only — the <c>Item</c> proto has no refine/enchant field — so per-class gear
/// carries <c>RefineLevel=0</c> / <c>Enchant=null</c> (a documented gap).</para>
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    // EquipAttr (proto stru_equip_attr) IL2CPP property handles, resolved once in Bootstrap. The roll
    // maps are map<int,int> = EquipAttrLibTable row id -> 0..100 percentile, matching
    // GearAttrRoll{LibRowId, Percentile} exactly. Field-number mapping mirrors
    // Stellar.Wire/GearInstanceReader.ReadEquipAttr (basic=10, advance=11, recast=12, rare=14,
    // perfection_value=7, perfection_level=13, max_perfection_value=15, equip_attr_set=17, break=18).
    private PropertyInfo? _itemEquipAttrProperty;                         // Item.EquipAttr (proto field 10)
    private PropertyInfo? _eaBasic, _eaAdvance, _eaRecast, _eaRare;       // top-level roll maps
    private PropertyInfo? _eaSet;                                         // EquipAttrSet (current spec's SCHOOL rolls)
    private PropertyInfo? _eaPerfVal, _eaPerfMax, _eaPerfLevel, _eaBreak; // scalar fields
    private PropertyInfo? _easBasic, _easAdvance, _easRecast, _easRare;   // EquipAttrSet sub-maps

    private static readonly IReadOnlyDictionary<int, ModuleInfo> EmptyModuleDict = new Dictionary<int, ModuleInfo>(0);

    // Resolves the EquipAttr / EquipAttrSet sub-property handles from the type registry (same pattern as
    // ResolveSubProperties for Item/ModNewAttr/ModInfo). Called from Bootstrap's ResolveSubProperties.
    private void ResolveEquipAttrProperties()
    {
        var eaType = _typeRegistry.FindType("Zproto.EquipAttr") ?? FindTypeByShortName("EquipAttr");
        if (eaType is not null)
        {
            _eaBasic = FindMapLikeProperty(eaType, "BasicAttr");
            _eaAdvance = FindMapLikeProperty(eaType, "AdvanceAttr");
            _eaRecast = FindMapLikeProperty(eaType, "RecastAttr");
            _eaRare = FindMapLikeProperty(eaType, "RareQualityAttr");
            _eaSet = eaType.GetProperty("EquipAttrSet", AnyInstance);
            _eaPerfVal = eaType.GetProperty("PerfectionValue", AnyInstance);
            _eaPerfMax = eaType.GetProperty("MaxPerfectionValue", AnyInstance);
            _eaPerfLevel = eaType.GetProperty("PerfectionLevel", AnyInstance);
            _eaBreak = eaType.GetProperty("BreakThroughTime", AnyInstance);
        }

        var setType = _typeRegistry.FindType("Zproto.EquipAttrSet") ?? FindTypeByShortName("EquipAttrSet");
        if (setType is not null)
        {
            _easBasic = FindMapLikeProperty(setType, "BasicAttr");
            _easAdvance = FindMapLikeProperty(setType, "AdvanceAttr");
            _easRecast = FindMapLikeProperty(setType, "RecastAttr");
            _easRare = FindMapLikeProperty(setType, "RareQualityAttr");
        }
    }

    /// <summary>
    /// Resolves EVERY saved loadout's per-class gear + modules from their slot → uuid maps (read by the
    /// loadout probe from <c>equipInfoMap</c>/<c>modInfoMap</c>), in one pass. Builds the uuid → item
    /// index ONCE (not per plan), then per plan: gear items decode to <see cref="GearInstance"/> (rolls
    /// via <see cref="ReadEquipAttrObject"/>), module items to <see cref="ModuleInfo"/> (parts via the
    /// existing <see cref="BuildModuleInfoOrNull"/>). Returns a same-length list of empties when the
    /// container isn't resolved yet (or holds no items) — the loadout probe treats an all-empty result
    /// as "not ready" and retries on later ticks. Result order matches <paramref name="plans"/>.
    /// </summary>
    internal IReadOnlyList<(IReadOnlyList<GearInstance> Gear, IReadOnlyDictionary<int, ModuleInfo> Modules)>
        ResolvePlanLoadouts(
            IReadOnlyList<(IReadOnlyDictionary<int, long> Equip, IReadOnlyDictionary<int, long> Mod)> plans)
    {
        if (!EnsureResolved()) return EmptyResults(plans.Count);
        var charSerialize = _readCharSerialize?.Invoke();
        if (charSerialize is null) return EmptyResults(plans.Count);

        var index = BuildUuidIndex(charSerialize);
        if (index.Count == 0) return EmptyResults(plans.Count);   // items not synced yet — retry

        var modInfosByUuid = ReadModInfosByUuid(charSerialize);
        var results = new List<(IReadOnlyList<GearInstance>, IReadOnlyDictionary<int, ModuleInfo>)>(plans.Count);
        foreach (var (equip, mod) in plans)
            results.Add((ResolveGear(equip, index), ResolveModules(mod, index, modInfosByUuid)));
        return results;
    }

    private static IReadOnlyList<(IReadOnlyList<GearInstance>, IReadOnlyDictionary<int, ModuleInfo>)> EmptyResults(int count)
    {
        var list = new List<(IReadOnlyList<GearInstance>, IReadOnlyDictionary<int, ModuleInfo>)>(count);
        for (var i = 0; i < count; i++) list.Add((Array.Empty<GearInstance>(), EmptyModuleDict));
        return list;
    }

    private List<GearInstance> ResolveGear(IReadOnlyDictionary<int, long> equipUuidsBySlot, Dictionary<long, object> index)
    {
        var gear = new List<GearInstance>(equipUuidsBySlot.Count);
        foreach (var (slot, uuid) in equipUuidsBySlot)
        {
            if (uuid == 0 || !index.TryGetValue(uuid, out var entry)) continue;
            gear.Add(ReadGearInstanceFromContainer(entry, slot, uuid));
        }
        gear.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
        return gear;
    }

    private Dictionary<int, ModuleInfo> ResolveModules(
        IReadOnlyDictionary<int, long> modUuidsBySlot, Dictionary<long, object> index, Dictionary<long, object>? modInfosByUuid)
    {
        var modules = new Dictionary<int, ModuleInfo>(modUuidsBySlot.Count);
        foreach (var (slot, uuid) in modUuidsBySlot)
        {
            if (uuid == 0 || !index.TryGetValue(uuid, out var entry)) continue;
            var m = BuildModuleInfoOrNull(entry, modInfosByUuid);
            if (m is not null) modules[slot] = m;
        }
        return modules;
    }

    // Index every owned item by uuid across ALL packages (mirrors the game's GetItemTabDataByUuid, which
    // scans itemPackage.packages[*].items[uuid]). A plan's gear/modules may live in any package, and a
    // non-active plan's items stay in the packages (confirmed in-game 2026-08-03).
    private Dictionary<long, object> BuildUuidIndex(object charSerialize)
    {
        var index = new Dictionary<long, object>(256);
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
            foreach (var entry in EnumerateMapValues(itemsMap))
            {
                if (entry is null) continue;
                long uuid = TryReadInt64(entry, _itemUuidProperty);
                if (uuid != 0) index[uuid] = entry;
            }
        }
        return index;
    }

    private GearInstance ReadGearInstanceFromContainer(object entry, int slot, long uuid)
    {
        int configId = TryReadInt32(entry, _itemConfigIdProperty);
        int quality = TryReadInt32(entry, _itemQualityProperty);
        object? equipAttr = SafeGet(_itemEquipAttrProperty, entry);
        var (perfection, attrs, breakThrough) = ReadEquipAttrObject(equipAttr);
        // Refine + enchant are equip-slot state (CharSerialize.equip.equipList), current-class only —
        // not on the item, so unavailable for a non-active plan. Default (documented gap).
        return new GearInstance(slot, uuid, configId, quality, 0, perfection, attrs, null, breakThrough);
    }

    private (GearPerfection Perfection, GearAttrRolls Attrs, int BreakThrough) ReadEquipAttrObject(object? equipAttr)
    {
        if (equipAttr is null) return (default, GearAttrRolls.Empty, 0);

        int pv = TryReadInt32(equipAttr, _eaPerfVal);
        int pMax = TryReadInt32(equipAttr, _eaPerfMax);
        int pLevel = TryReadInt32(equipAttr, _eaPerfLevel);
        int breakThrough = TryReadInt32(equipAttr, _eaBreak);

        var basic = ReadRollMap(equipAttr, _eaBasic, school: false);
        var advanced = ReadRollMap(equipAttr, _eaAdvance, school: false);
        var recast = ReadRollMap(equipAttr, _eaRecast, school: false);
        var rare = ReadRollMap(equipAttr, _eaRare, school: false);

        // equip_attr_set (the current spec's SCHOOL rolls) is PREFERRED over the top-level maps — same
        // precedence as GearInstanceReader.Pick.
        object? set = SafeGet(_eaSet, equipAttr);
        if (set is not null)
        {
            basic = Prefer(ReadRollMap(set, _easBasic, school: true), basic);
            advanced = Prefer(ReadRollMap(set, _easAdvance, school: true), advanced);
            recast = Prefer(ReadRollMap(set, _easRecast, school: true), recast);
            rare = Prefer(ReadRollMap(set, _easRare, school: true), rare);
        }

        var attrs = basic.Count == 0 && advanced.Count == 0 && recast.Count == 0 && rare.Count == 0
            ? GearAttrRolls.Empty
            : new GearAttrRolls(basic, advanced, recast, rare);
        return (new GearPerfection(pv, pMax, pLevel), attrs, breakThrough);
    }

    private static List<GearAttrRoll> Prefer(List<GearAttrRoll> school, List<GearAttrRoll> top)
        => school.Count > 0 ? school : top;

    private List<GearAttrRoll> ReadRollMap(object owner, PropertyInfo? prop, bool school)
    {
        var list = new List<GearAttrRoll>(4);
        object? map = SafeGet(prop, owner);
        if (map is null) return list;
        foreach (var (k, v) in EnumerateMapEntries(map))
        {
            int libRowId = AsInt32(k);
            if (libRowId == 0) continue;
            list.Add(new GearAttrRoll(libRowId, AsInt32(v), school));
        }
        return list;
    }

    private static object? SafeGet(PropertyInfo? prop, object? owner)
    {
        if (prop is null || owner is null) return null;
        try { return prop.GetValue(owner); }
        catch { return null; }
    }
}
