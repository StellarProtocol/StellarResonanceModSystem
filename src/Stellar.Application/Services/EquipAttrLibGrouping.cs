using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Application.Services;

/// <summary>One EquipAttrLib table row as the Infrastructure loader emits it: the row's grouping
/// lib id, the slot-part codes the row applies to (<c>AllowPart</c> — a lib id has one row per
/// part group), and its paired attr entries. The service indexes rows BOTH by row id (the wire's
/// per-instance roll-map key space) and by AttrLibId (the equip-row lib-reference space).</summary>
internal readonly record struct EquipAttrLibRowData(int AttrLibId, int[] AllowParts, EquipAttrRange[] Entries);

/// <summary>One EquipEnchantItem table row (a gem at a level): the index keys (type id + level) plus the
/// gem item id (whose name carries the display level) and the gem's flat effects.</summary>
internal readonly record struct EnchantItemRowData(int TypeId, int Level, int GemItemId, EnchantEffect[] Effects);

/// <summary>One EquipAttrSchoolLib table row (v2 spec/school advanced rolls): like
/// <see cref="EquipAttrLibRowData"/> plus the talent-school ids the row applies to — a lib id has one
/// row per (part group × talent school), so the lookup filters by both.</summary>
internal readonly record struct EquipAttrSchoolLibRowData(
    int AttrLibId, int[] AllowParts, int[] TalentSchoolIds, EquipAttrRange[] Entries);

/// <summary>
/// Pure pairing and regroup logic for EquipAttrLib rows, extracted from Infrastructure
/// so it can be unit-tested without game interop assemblies.
/// </summary>
internal static class EquipAttrLibGrouping
{
    /// <summary>
    /// Pairs AttrEffect[i] (= [count, attrId]) with AttrEffectConfig[i] (= [min, max]) by index.
    /// The index-pairing contract: AttrEffect[i] and AttrEffectConfig[i] describe the same attr slot;
    /// the two arrays are always the same length in well-formed rows.
    /// Entries with a malformed pair (too few elements in the effect array) are skipped.
    /// A missing config entry defaults to min=0, max=0.
    /// </summary>
    internal static EquipAttrRange[] PairAttrEntries(int[][] effects, int[][] configs)
    {
        if (effects.Length == 0) return Array.Empty<EquipAttrRange>();
        var entries = new List<EquipAttrRange>(effects.Length);
        for (var i = 0; i < effects.Length; i++)
        {
            var effect = effects[i];
            if (effect.Length < 2) continue;
            var attrId = effect[1];
            var min = 0;
            var max = 0;
            if (i < configs.Length && configs[i].Length >= 2)
            {
                min = configs[i][0];
                max = configs[i][1];
            }
            entries.Add(new EquipAttrRange(attrId, min, max));
        }
        return entries.ToArray();
    }

    /// <summary>
    /// Regroups lib rows into the AttrLibId-keyed lookup consumers use, keeping rows that share an
    /// AttrLibId SEPARATE (not concatenated): a lib id has one row per slot-part(/talent) group and the
    /// part-filtered lookup picks one — the old concatenating merge mixed other slots' ranges into every
    /// item's display ("all attrs 337–1,908", in-world 2026-06-13; ZDPS filters by AllowPart). Rows with
    /// libId == 0 or no entries are skipped. Generic over the v1 and v2 (school) row types.
    /// </summary>
    internal static IReadOnlyDictionary<int, IReadOnlyList<T>> RegroupByLibId<T>(
        IEnumerable<T> rows, Func<T, int> libIdOf, Func<T, bool> hasEntries)
    {
        var result = new Dictionary<int, List<T>>();
        foreach (var row in rows)
        {
            var lib = libIdOf(row);
            if (lib == 0 || !hasEntries(row)) continue;
            if (!result.TryGetValue(lib, out var list)) result[lib] = list = new List<T>(2);
            list.Add(row);
        }
        var ro = new Dictionary<int, IReadOnlyList<T>>(result.Count);
        foreach (var kv in result) ro[kv.Key] = kv.Value;
        return ro;
    }
}
