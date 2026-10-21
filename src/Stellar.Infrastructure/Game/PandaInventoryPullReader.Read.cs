using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Property-walking logic for <see cref="PandaInventoryPullReader"/>. Consumes
/// the reflection handles resolved in the Bootstrap partial and surfaces
/// the two snapshot shapes:
/// <list type="bullet">
///   <item><c>ReadModuleList</c> — full module-package inventory, filtered
///         to items whose <c>ModNewAttr.ModParts</c> is non-empty
///         (the canonical "is this a module?" check per the reference
///         Python tool).</item>
///   <item><c>ReadEquippedSlots</c> — <c>Mod.ModSlots</c> as a
///         <c>Dictionary&lt;int, long&gt;</c>.</item>
/// </list>
/// </summary>
internal sealed partial class PandaInventoryPullReader
{
    private IReadOnlyList<ModuleInfo> ReadModuleList(object charSerialize)
    {
        // Walk: CharSerialize.ItemPackage.Packages → find entry with key == ModPackageKey
        //       package.Items → enumerate, filter where ModNewAttr.ModParts.Count > 0
        //       build ModuleInfo per item, pairing ModParts[i] with its magnitude from
        //       Item.ModPartLevel.Levels[attrId] (release_3.7_2+), falling back to the
        //       positional ModInfos[uuid].InitLinkNums[i] for older builds.
        if (_itemPackageProperty is null || _packagesProperty is null) return Array.Empty<ModuleInfo>();
        if (_itemUuidProperty is null || _itemConfigIdProperty is null) return Array.Empty<ModuleInfo>();

        var itemsMap = GetModPackageItemsMapOrNull(charSerialize);
        if (itemsMap is null) return Array.Empty<ModuleInfo>();

        // ModInfos: same access pattern from CharSerialize.Mod.ModInfos.
        var modInfosByUuid = ReadModInfosByUuid(charSerialize);

        return WalkItemsMapToModules(itemsMap, modInfosByUuid);
    }

    // Navigates CharSerialize → ItemPackage → Packages[ModPackageKey] → Items.
    // Returns null (and fires OnReadAbort) if any step fails.
    private object? GetModPackageItemsMapOrNull(object charSerialize)
    {
        object? itemPackage;
        try { itemPackage = _itemPackageProperty!.GetValue(charSerialize); }
        catch { return null; }
        if (itemPackage is null)
        {
            OnReadAbort(ReadAbort.ItemPackageNull);
            return null;
        }

        object? packagesMap;
        try { packagesMap = _packagesProperty!.GetValue(itemPackage); }
        catch { return null; }
        if (packagesMap is null)
        {
            OnReadAbort(ReadAbort.ItemPackageNull);
            return null;
        }

        // Locate the package whose key == ModPackageKey by enumerating the
        // MapField. MapField<TKey, TValue> implements both BCL IEnumerable
        // and IDictionary, so KeyValuePair-style iteration works.
        var modPackage = LookupMapValue(packagesMap, ModPackageKey);
        if (modPackage is null)
        {
            OnReadAbort(ReadAbort.ModPackageAbsent);
            return null;
        }

        // Lazily resolve PackageContainerArchive.Items + sub-types on the
        // first concrete instance we hold.
        if (_packageItemsProperty is null)
        {
            _packageItemsProperty = FindMapLikeProperty(modPackage.GetType(), "Items");
            if (_packageItemsProperty is null) return null;
        }

        object? itemsMap;
        try { itemsMap = _packageItemsProperty.GetValue(modPackage); }
        catch { return null; }
        if (itemsMap is null)
        {
            OnReadAbort(ReadAbort.EmptyModPackage);
            return null;
        }
        return itemsMap;
    }

    // Iterates itemsMap, builds a ModuleInfo per module entry, and reports
    // walked/filtered counts to the diagnostic hooks.
    private List<ModuleInfo> WalkItemsMapToModules(object itemsMap, Dictionary<long, object>? modInfosByUuid)
    {
        var walked = 0;
        var result = new List<ModuleInfo>(capacity: 16);
        foreach (var entry in EnumerateMapValues(itemsMap))
        {
            walked++;
            if (entry is null) continue;
            var info = BuildModuleInfoOrNull(entry, modInfosByUuid);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        if (walked == 0)
        {
            OnReadAbort(ReadAbort.EmptyModPackage);
        }
        else
        {
            OnReadOk(walked, result.Count);
        }
        return result;
    }

    private Dictionary<long, object>? ReadModInfosByUuid(object charSerialize)
    {
        if (_modProperty is null || _modInfosProperty is null) return null;
        object? modContainer;
        try { modContainer = _modProperty.GetValue(charSerialize); }
        catch { return null; }
        if (modContainer is null) return null;
        object? modInfosMap;
        try { modInfosMap = _modInfosProperty.GetValue(modContainer); }
        catch { return null; }
        if (modInfosMap is null) return null;
        return MaterializeKeyedDict<long>(modInfosMap);
    }

    // Returns null if the item is not a module (no ModParts) or if any
    // critical reflection step fails.
    private ModuleInfo? BuildModuleInfoOrNull(object entry, Dictionary<long, object>? modInfosByUuid)
    {
        // Filter to modules: ModNewAttr.ModParts must be non-empty.
        var modParts = ResolveModParts(entry);
        if (modParts is null || modParts.Count == 0) return null;

        // Uuid + ConfigId + Quality.
        long uuid = TryReadInt64(entry, _itemUuidProperty);
        int configId = TryReadInt32(entry, _itemConfigIdProperty);
        int quality = _itemQualityProperty is null ? 0 : TryReadInt32(entry, _itemQualityProperty);

        var parts = BuildModulePartsForEntry(entry, modParts, uuid, modInfosByUuid);

        var category = LookupCategoryByConfigId(configId);
        // Item display name is not directly available from inventory;
        // ConfigId resolves to ModTableBase.Name in the GameData layer
        // (Phase 5 extension - deferred per the spec).
        return new ModuleInfo(uuid, configId, string.Empty, quality, category, parts);
    }

    // Resolves ModNewAttr.ModParts for the given item entry. Returns null if
    // the entry has no ModNewAttr (not a module) or the property cannot be found.
    private IList<int>? ResolveModParts(object entry)
    {
        object? modNewAttr = null;
        if (_itemModNewAttrProperty is not null)
        {
            try { modNewAttr = _itemModNewAttrProperty.GetValue(entry); }
            catch { modNewAttr = null; }
        }
        if (modNewAttr is null) return null;

        if (_modNewAttrPartsProperty is null)
        {
            _modNewAttrPartsProperty = FindMapLikeProperty(modNewAttr.GetType(), "ModParts");
            if (_modNewAttrPartsProperty is null) return null;
        }

        try
        {
            var raw = _modNewAttrPartsProperty.GetValue(modNewAttr);
            return CollectInts(raw);
        }
        catch { return null; }
    }

    // Reads InitLinkNums for the given uuid from the modInfosByUuid map.
    private IList<int> ReadInitLinkNums(long uuid, Dictionary<long, object>? modInfosByUuid)
    {
        if (modInfosByUuid is null) return Array.Empty<int>();
        if (!modInfosByUuid.TryGetValue(uuid, out var modInfo) || modInfo is null) return Array.Empty<int>();
        if (_modInfoInitLinkNumsProperty is null) return Array.Empty<int>();
        try
        {
            var raw = _modInfoInitLinkNumsProperty.GetValue(modInfo);
            return CollectInts(raw);
        }
        catch { return Array.Empty<int>(); }
    }

    // Produces the per-attribute magnitude list for a module item.
    //
    // release_3.7_2 moved where the magnitudes live. Pre-3.7_2 they sat
    // positionally in Mod.ModInfos[uuid].InitLinkNums, paired by index with
    // ModParts. On 3.7_2 Mod.ModInfos is empty at runtime (so InitLinkNums reads
    // nothing → every value 0) and the magnitudes live on the item itself as
    // Item.ModPartLevel.Levels, a MapField<int,int> keyed by the SAME attrId
    // that appears in ModParts.
    //
    // PRIMARY: read ModPartLevel.Levels and look up each attrId (0 if absent).
    // FALLBACK: when the item carries no readable/non-empty Levels (a pre-3.7_2
    // build where ModPartLevel is absent but InitLinkNums is populated), use the
    // original positional InitLinkNums[i] pairing.
    private List<ModulePart> BuildModulePartsForEntry(
        object entry, IList<int> modParts, long uuid, Dictionary<long, object>? modInfosByUuid)
    {
        var levels = ReadModPartLevels(entry);
        if (levels is not null && levels.Count > 0)
        {
            return BuildModulePartsFromLevels(modParts, levels);
        }

        // Back-compat path for pre-3.7_2 builds.
        var initLinkNums = ReadInitLinkNums(uuid, modInfosByUuid);
        return BuildModuleParts(modParts, initLinkNums);
    }

    // Reads the item's ModPartLevel.Levels map (attrId → magnitude) into a
    // Dictionary, or null when the item carries no readable ModPartLevel (the
    // caller then falls back to InitLinkNums). The Levels map property is resolved
    // lazily off the first concrete ModPartLevel instance when Bootstrap couldn't
    // pin it from the static property type (mirrors ResolveModParts). A reflection
    // miss degrades to null — never throws into the walk loop.
    private Dictionary<int, int>? ReadModPartLevels(object entry)
    {
        if (_itemModPartLevelProperty is null) return null;

        object? modPartLevel;
        try { modPartLevel = _itemModPartLevelProperty.GetValue(entry); }
        catch { return null; }
        if (modPartLevel is null) return null;

        if (_modPartLevelsProperty is null)
        {
            _modPartLevelsProperty = FindMapLikeProperty(modPartLevel.GetType(), "Levels");
            if (_modPartLevelsProperty is null) return null;
        }

        object? levelsMap;
        try { levelsMap = _modPartLevelsProperty.GetValue(modPartLevel); }
        catch { return null; }
        if (levelsMap is null) return null;

        try
        {
            var result = new Dictionary<int, int>(capacity: 4);
            foreach (var (k, v) in EnumerateMapEntries(levelsMap))
            {
                result[AsInt32(k)] = AsInt32(v);
            }
            return result;
        }
        catch { return null; }
    }

    // Pairs modParts[i] with initLinkNums[i] to produce the ModulePart list.
    private static List<ModulePart> BuildModuleParts(IList<int> modParts, IList<int> initLinkNums)
    {
        var parts = new List<ModulePart>(modParts.Count);
        for (var i = 0; i < modParts.Count; i++)
        {
            int attrId = modParts[i];
            int value = i < initLinkNums.Count ? initLinkNums[i] : 0;
            parts.Add(new ModulePart(attrId, string.Empty, value));
        }
        return parts;
    }

    // Pairs each attrId in modParts with its magnitude from the ModPartLevel.Levels
    // map (0 when the key is absent). ModulePart's shape is unchanged — only the
    // source of `value` differs from the positional InitLinkNums path.
    private static List<ModulePart> BuildModulePartsFromLevels(IList<int> modParts, Dictionary<int, int> levels)
    {
        var parts = new List<ModulePart>(modParts.Count);
        for (var i = 0; i < modParts.Count; i++)
        {
            int attrId = modParts[i];
            int value = levels.TryGetValue(attrId, out var v) ? v : 0;
            parts.Add(new ModulePart(attrId, string.Empty, value));
        }
        return parts;
    }

    internal IReadOnlyDictionary<int, long> ReadEquippedSlots(object charSerialize)
    {
        if (_modProperty is null || _modSlotsProperty is null)
        {
            return new Dictionary<int, long>(0);
        }
        object? modContainer;
        try { modContainer = _modProperty.GetValue(charSerialize); }
        catch { return new Dictionary<int, long>(0); }
        if (modContainer is null) return new Dictionary<int, long>(0);

        object? slotsMap;
        try { slotsMap = _modSlotsProperty.GetValue(modContainer); }
        catch { return new Dictionary<int, long>(0); }
        if (slotsMap is null) return new Dictionary<int, long>(0);

        var result = new Dictionary<int, long>(capacity: 5);
        foreach (var (key, value) in EnumerateMapEntries(slotsMap))
        {
            int slot = AsInt32(key);
            long uuid = AsInt64(value);
            if (slot > 0)
            {
                result[slot] = uuid;
            }
        }
        return result;
    }

    private ModuleCategory LookupCategoryByConfigId(int configId)
    {
        if (_categoryByConfigId.TryGetValue(configId, out var cached))
        {
            return cached;
        }
        if (_modTableBaseType is null || _modTableGetTableMethod is null || _modTypeProperty is null)
        {
            return ModuleCategory.Attack;
        }

        if (!EnsureCachedModTable()) return ModuleCategory.Attack;
        if (_modTableGetItem is null) return ModuleCategory.Attack;

        var category = LookupModTypeFromTable(configId);
        _categoryByConfigId[configId] = category;
        return category;
    }

    // Lazily loads the mod table singleton and caches its ContainsKey/get_Item
    // method handles. Returns false if the table cannot be obtained.
    private bool EnsureCachedModTable()
    {
        if (_cachedModTable is not null) return true;
        try
        {
            var parameters = _modTableGetTableMethod!.GetParameters();
            _cachedModTable = parameters.Length == 0
                ? _modTableGetTableMethod.Invoke(null, Array.Empty<object>())
                : _modTableGetTableMethod.Invoke(null, new object[] { true });
        }
        catch { _cachedModTable = null; }
        if (_cachedModTable is null) return false;
        _modTableContainsKey = _cachedModTable.GetType().GetMethod("ContainsKey", AnyInstance);
        _modTableGetItem = _cachedModTable.GetType().GetMethod("get_Item", AnyInstance);
        return true;
    }

    // Looks up a row in the cached mod table by configId and maps its ModType
    // field to a ModuleCategory. Returns Attack on any failure.
    private ModuleCategory LookupModTypeFromTable(int configId)
    {
        var keyArg = new object[] { configId };
        try
        {
            if (_modTableContainsKey is not null && _modTableContainsKey.Invoke(_cachedModTable, keyArg) is not true)
            {
                return ModuleCategory.Attack;
            }
            var row = _modTableGetItem!.Invoke(_cachedModTable, keyArg);
            if (row is null) return ModuleCategory.Attack;
            var raw = _modTypeProperty!.GetValue(row);
            int modType = raw switch
            {
                int i => i,
                long l => unchecked((int)l),
                _ => 0,
            };
            return modType switch
            {
                1 => ModuleCategory.Attack,
                2 => ModuleCategory.Assistant,
                3 => ModuleCategory.Defend,
                _ => ModuleCategory.Attack,
            };
        }
        catch
        {
            return ModuleCategory.Attack;
        }
    }
}
