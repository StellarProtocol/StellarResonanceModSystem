using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    // ===== Equip (gear-slot) tables — deferred batch ======================
    // Column shapes confirmed against ZDPS's JSON extraction of the same tables
    // (EquipTable.json etc.); the first-row diagnostics below log the live
    // shapes (including raw property type names for the nested-array columns)
    // so a patch-day drift is visible in the BepInEx log.

    private IReadOnlyDictionary<int, EquipRowInfo> LoadEquipRows()
    {
        var firstLogged = false;
        return LoadDeferredTable<EquipRowInfo>(
            label: "EquipRow",
            typeName: "Bokura.EquipTableBase",
            capacityHint: 4992,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);
                var part     = ReadInt(row, rowType, "EquipPart");
                var gs       = ReadInt(row, rowType, "EquipGs");
                // Live shape: WearCondition is an Int32Table ([[60]]-style) while the
                // JSON extraction shows flat lists; PerfectUpperLimit is flat ([100,100]).
                // The flexible reader handles both (1D wrapper, or first row of a 2D one).
                var wear     = FirstOrZero(ReadIntsFlexible(row, rowType, "WearCondition"));
                var perfect  = FirstOrZero(ReadIntsFlexible(row, rowType, "PerfectUpperLimit"));
                // The FIRST element of each lib-id column is a VERSION marker, not a lib id
                // (ZDPS BuildAttrListFromLibIds: 1 = EquipAttrLib table, 2 = school/spec lib table).
                // Treating it as a lib id duplicated/garbled the gear-detail attr sections.
                var (basicVer, basics)   = SplitLibVersion(ReadInt32Array(row, rowType, "BasicAttrLibId"));
                var (advVer, advanced)   = SplitLibVersion(ReadInt32Array(row, rowType, "AdvancedAttrLibId"));
                var info = new EquipRowInfo(id, part, gs, wear, perfect, basicVer, basics, advVer, advanced);

                if (!firstLogged)
                {
                    firstLogged = true;
                    LogFirstEquipRow(info, rowType);
                }

                return (id, info);
            });
    }

    private static int FirstOrZero(int[] a) => a.Length > 0 ? a[0] : 0;

    private static (int Version, int[] LibIds) SplitLibVersion(int[] raw)
        => raw.Length == 0 ? (1, raw) : (raw[0], raw[1..]);

    // Rows are emitted ROW-keyed (the wire's per-instance roll-map key space); the Application-side
    // service builds both indexes from this (row id + regrouped AttrLibId).
    private IReadOnlyDictionary<int, EquipAttrLibRowData> LoadEquipAttrLibs()
    {
        var firstLogged = false;
        return LoadDeferredTable<EquipAttrLibRowData>(
            label: "EquipAttrLib",
            typeName: "Bokura.EquipAttrLibTableBase",
            capacityHint: 4992,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);
                var attrLibId = ReadInt(row, rowType, "AttrLibId");
                // The slot-part codes this row applies to — a lib id has one row per part group, and
                // the Application lookup is part-filtered (ZDPS AllowPart semantics).
                var allowParts = ReadInt32Array(row, rowType, "AllowPart");
                // AttrEffect[i] = [count, attrId]; AttrEffectConfig[i] = [min, max].
                var effects = ReadInt32Array2D(row, rowType, "AttrEffect");
                var configs = ReadInt32Array2D(row, rowType, "AttrEffectConfig");
                var entries = EquipAttrLibGrouping.PairAttrEntries(effects, configs);

                if (!firstLogged)
                {
                    firstLogged = true;
                    LogFirstEquipAttrLibRow(new EquipAttrLibDiagInfo(id, attrLibId, effects, configs, entries), rowType);
                }

                return (id, new EquipAttrLibRowData(attrLibId, allowParts, entries));
            });
    }

    // v2 SCHOOL attr-lib table (Bokura.EquipAttrSchoolLibTableBase): same shape as EquipAttrLib plus the
    // TalentSchoolId list (which specs the row applies to). Spec-dependent advanced rolls for raid gear —
    // resolved via the inspected player's spec (ProfessionSpecs.TalentSchool) so far players' v2 rolls show.
    private IReadOnlyDictionary<int, EquipAttrSchoolLibRowData> LoadEquipSchoolAttrLibs()
    {
        var firstLogged = false;
        return LoadDeferredTable<EquipAttrSchoolLibRowData>(
            label: "EquipSchoolAttrLib",
            typeName: "Bokura.EquipAttrSchoolLibTableBase",
            capacityHint: 4992,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);
                var attrLibId  = ReadInt(row, rowType, "AttrLibId");
                var allowParts = ReadInt32Array(row, rowType, "AllowPart");
                var talents    = ReadInt32Array(row, rowType, "TalentSchoolId");
                var effects    = ReadInt32Array2D(row, rowType, "AttrEffect");
                var configs    = ReadInt32Array2D(row, rowType, "AttrEffectConfig");
                var entries    = EquipAttrLibGrouping.PairAttrEntries(effects, configs);

                if (!firstLogged) { firstLogged = true; LogFirstSchoolLibRow(new SchoolLibDiagInfo(id, attrLibId, allowParts, talents, entries), row, rowType); }

                return (id, new EquipAttrSchoolLibRowData(attrLibId, allowParts, talents, entries));
            });
    }

    // EquipEnchantItem table (Bokura.EquipEnchantItemTableBase): socketed gems. Indexed downstream by
    // (EnchantItemTypeId, EnchantItemLevel) — the wire enchant pair. The DISPLAY level lives in the gem
    // item's name (row.Id → item table), so we surface that item id; the wire enchant_level is just the
    // index. EnchantItemEffect[k]=[type,attrId] pairs with EnchantItemPar[k]=[value] (flat, no rolls).
    private IReadOnlyDictionary<int, EnchantItemRowData> LoadEquipEnchantItems()
    {
        var firstLogged = false;
        return LoadDeferredTable<EnchantItemRowData>(
            label: "EquipEnchantItem",
            typeName: "Bokura.EquipEnchantItemTableBase",
            capacityHint: 2048,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);
                var typeId  = ReadInt(row, rowType, "EnchantItemTypeId");
                var level   = ReadInt(row, rowType, "EnchantItemLevel");
                var effects = ReadInt32Array2D(row, rowType, "EnchantItemEffect");
                var pars    = ReadInt32Array2D(row, rowType, "EnchantItemPar");
                var data = new EnchantItemRowData(typeId, level, id, PairEnchantEffects(effects, pars));
                if (!firstLogged) { firstLogged = true; LogFirstEnchantItemRow(data, row, rowType); }
                return (id, data);
            });
    }

    // EnchantItemEffect[k] = [type, attrId] (type 1 = Attr); EnchantItemPar[k] = [value]. One flat
    // EnchantEffect per Attr-typed entry; non-attr (buff/temp) entries are skipped for the gear panel.
    private static EnchantEffect[] PairEnchantEffects(int[][] effects, int[][] pars)
    {
        var list = new List<EnchantEffect>(effects.Length);
        for (var i = 0; i < effects.Length; i++)
        {
            var e = effects[i];
            if (e.Length < 2 || e[0] != 1) continue;   // 1 = RemodelInfoType.Attr
            var value = i < pars.Length && pars[i].Length > 0 ? pars[i][0] : 0;
            list.Add(new EnchantEffect(e[1], value));
        }
        return list.ToArray();
    }

    // LoadDeferredTable needs a struct TInfo — unwrap to plain strings after the walk.
    private readonly record struct EquipSlotNameRow(string Name);

    private IReadOnlyDictionary<int, string> LoadEquipSlotNames()
    {
        var firstLogged = false;
        var rows = LoadDeferredTable<EquipSlotNameRow>(
            label: "EquipPart",
            typeName: "Bokura.EquipPartTableBase",
            capacityHint: 32,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);
                // Live column is PartName (String, e.g. 'Weapon'); "Name" kept as fallback.
                var name = ReadStringOrMlString(row, rowType, "PartName");
                if (string.IsNullOrEmpty(name)) name = ReadStringOrMlString(row, rowType, "Name");

                if (!firstLogged)
                {
                    firstLogged = true;
                    LogFirstEquipPartRow(id, name, row, rowType);
                }

                return (id, new EquipSlotNameRow(name));
            });

        var result = new Dictionary<int, string>(rows.Count);
        foreach (var kv in rows) result[kv.Key] = kv.Value.Name;
        return result;
    }
}
