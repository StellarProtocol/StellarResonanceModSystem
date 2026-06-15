using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class GameDataEquipService : IGameDataEquip
{
    // Same pattern as GameDataCombatService: each cache is a dictionary read
    // lock-free via Volatile.Read and replaced atomically via Volatile.Write.
    // Build happens on the game thread; reads happen on any thread.
    private static readonly IReadOnlyList<EquipAttrRange> EmptyLib = Array.Empty<EquipAttrRange>();

    private IReadOnlyDictionary<int, EquipRowInfo>?                              _rows;
    private IReadOnlyDictionary<int, IReadOnlyList<EquipAttrLibRowData>>?        _libs;        // keyed by AttrLibId
    private IReadOnlyDictionary<int, IReadOnlyList<EquipAttrSchoolLibRowData>>?  _schoolLibs;  // v2, keyed by AttrLibId
    private IReadOnlyDictionary<int, IReadOnlyList<EquipAttrRange>>?             _libRows;     // keyed by table row Id
    private IReadOnlyDictionary<int, IReadOnlyList<EquipAttrRange>>?             _schoolLibRows; // v2, keyed by row Id
    private IReadOnlyDictionary<int, string>?                                    _slots;
    private IReadOnlyDictionary<(int Type, int Level), EnchantItemInfo>?         _enchants;  // gem by (typeId, level)

    public EquipRowInfo? GetEquipRow(int equipId)
    {
        var cache = Volatile.Read(ref _rows);
        return cache != null && cache.TryGetValue(equipId, out var row) ? row : (EquipRowInfo?)null;
    }

    // Part-filtered (ZDPS semantics): a lib id has one row per slot-part group — pick the row whose
    // AllowPart contains the part. A row with NO AllowParts matches any part (defensive: if the live
    // column is absent/renamed the lookup degrades to first-row instead of empty).
    public IReadOnlyList<EquipAttrRange> GetAttrLib(int attrLibId, int equipPart)
    {
        var cache = Volatile.Read(ref _libs);
        if (cache == null || !cache.TryGetValue(attrLibId, out var rows)) return EmptyLib;
        foreach (var row in rows)
            if (row.AllowParts.Length == 0 || Array.IndexOf(row.AllowParts, equipPart) >= 0)
                return row.Entries;
        return EmptyLib;
    }

    // v2 school libs: filter by part AND talent school (a lib id has one row per part×school group).
    // A row with no AllowParts / no TalentSchoolIds matches any (defensive vs an absent/renamed column).
    public IReadOnlyList<EquipAttrRange> GetSchoolAttrLib(int attrLibId, int equipPart, int talentSchoolId)
    {
        var cache = Volatile.Read(ref _schoolLibs);
        if (cache == null || !cache.TryGetValue(attrLibId, out var rows)) return EmptyLib;
        foreach (var row in rows)
        {
            var partOk = row.AllowParts.Length == 0 || Array.IndexOf(row.AllowParts, equipPart) >= 0;
            var schoolOk = row.TalentSchoolIds.Length == 0 || Array.IndexOf(row.TalentSchoolIds, talentSchoolId) >= 0;
            if (partOk && schoolOk) return row.Entries;
        }
        return EmptyLib;
    }

    // v1 per-instance rolls key by the v1 lib-table ROW id.
    public IReadOnlyList<EquipAttrRange> GetAttrLibRow(int rowId)
    {
        var v1 = Volatile.Read(ref _libRows);
        return v1 != null && v1.TryGetValue(rowId, out var lib) ? lib : EmptyLib;
    }

    // v2 (spec-set) rolls key by the SCHOOL-table row id. Kept SEPARATE from GetAttrLibRow: a school row
    // id can collide with a v1 row id that maps to different attrs, so a v1-first merged lookup returned
    // the wrong attrs for spec gear (showed DMG-Bonus/Element-Resist instead of Crit/Luck, 2026-06-13).
    public IReadOnlyList<EquipAttrRange> GetSchoolAttrLibRow(int rowId)
    {
        var v2 = Volatile.Read(ref _schoolLibRows);
        return v2 != null && v2.TryGetValue(rowId, out var slib) ? slib : EmptyLib;
    }

    public string? GetSlotName(int equipPart)
    {
        var cache = Volatile.Read(ref _slots);
        return cache != null && cache.TryGetValue(equipPart, out var name) ? name : null;
    }

    public EnchantItemInfo? GetEnchantItem(int enchantTypeId, int enchantLevel)
    {
        var cache = Volatile.Read(ref _enchants);
        return cache != null && cache.TryGetValue((enchantTypeId, enchantLevel), out var info) ? info : (EnchantItemInfo?)null;
    }

    internal void LoadEquipRows(IReadOnlyDictionary<int, EquipRowInfo> cache)
        => Volatile.Write(ref _rows, cache);

    /// <summary>Indexes the loader's row-keyed data BOTH ways: by row id (the wire's per-instance
    /// roll-map key space) and regrouped by AttrLibId (the equip-row lib-reference space, rows kept
    /// separate for the part-filtered lookup).</summary>
    internal void LoadAttrLibs(IReadOnlyDictionary<int, EquipAttrLibRowData> rowData)
    {
        var byRow = new Dictionary<int, IReadOnlyList<EquipAttrRange>>(rowData.Count);
        var rows = new List<EquipAttrLibRowData>(rowData.Count);
        foreach (var kv in rowData)
        {
            if (kv.Value.Entries.Length > 0) byRow[kv.Key] = kv.Value.Entries;
            rows.Add(kv.Value);
        }
        Volatile.Write(ref _libRows, byRow);
        Volatile.Write(ref _libs, EquipAttrLibGrouping.RegroupByLibId(rows, r => r.AttrLibId, r => r.Entries.Length > 0));
    }

    /// <summary>Indexes the v2 school-lib rows by AttrLibId (part+talent-school filtered lookup) AND by
    /// row id (self's per-instance roll expansion — those keys live in the school table for v2 gear).</summary>
    internal void LoadSchoolAttrLibs(IReadOnlyDictionary<int, EquipAttrSchoolLibRowData> rowData)
    {
        var rows = new List<EquipAttrSchoolLibRowData>(rowData.Count);
        var byRow = new Dictionary<int, IReadOnlyList<EquipAttrRange>>(rowData.Count);
        foreach (var kv in rowData)
        {
            rows.Add(kv.Value);
            if (kv.Value.Entries.Length > 0) byRow[kv.Key] = kv.Value.Entries;
        }
        Volatile.Write(ref _schoolLibRows, byRow);
        Volatile.Write(ref _schoolLibs, EquipAttrLibGrouping.RegroupByLibId(rows, r => r.AttrLibId, r => r.Entries.Length > 0));
    }

    /// <summary>Indexes gem rows by (type id, level) so a wire enchant pair resolves to the gem item id
    /// (whose name carries the display level) + flat effects.</summary>
    internal void LoadEnchantItems(IReadOnlyDictionary<int, EnchantItemRowData> rowData)
    {
        var map = new Dictionary<(int, int), EnchantItemInfo>(rowData.Count);
        foreach (var kv in rowData)
        {
            var r = kv.Value;
            map[(r.TypeId, r.Level)] = new EnchantItemInfo(r.GemItemId, r.Effects);
        }
        Volatile.Write(ref _enchants, map);
    }

    internal void LoadSlotNames(IReadOnlyDictionary<int, string> cache)
        => Volatile.Write(ref _slots, cache);
}
