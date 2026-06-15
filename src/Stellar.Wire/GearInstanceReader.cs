using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.Wire;

/// <summary>Pure parser for the LOCAL player's equipped-gear instances out of a raw
/// <c>CharSerialize</c> payload (the method-21 <c>SyncContainerData.VData</c> bytes the
/// inventory capture already extracts). Joins <c>CharSerialize.equip</c> (field 12,
/// <c>EquipList</c>: slot → item uuid + refine level + enchant map) with
/// <c>CharSerialize.item_package</c> (field 7, <c>ItemPackage.packages[2]</c> =
/// PackageEquip) whose <c>Item.equip_attr</c> carries the rolled attribute maps and
/// perfection. Defensive Try* pattern — malformed input yields a partial or empty
/// list, never an exception.</summary>
public static class GearInstanceReader
{
    private const int PackageEquip = 2; // zproto EItemPackageType.PackageEquip

    /// <summary>Decode the equipped-gear instances, ordered by slot. Returns an empty
    /// list (never null) when nothing parses.</summary>
    public static IReadOnlyList<GearInstance> Read(ReadOnlySpan<byte> charSerialize)
    {
        // Single top-level walk capturing the two sub-messages we join.
        ReadOnlySpan<byte> itemPackage = default, equipList = default;
        int pos = 0;
        while (pos < charSerialize.Length)
        {
            if (!WireProtocol.TryReadTag(charSerialize, ref pos, out var field, out var wire)) break;
            if (wire == 2 && field is 7 or 12)
            {
                if (!WireProtocol.TryReadLengthDelimited(charSerialize, ref pos, out var inner)) break;
                if (field == 7) itemPackage = inner; else equipList = inner;
            }
            else if (!WireProtocol.SkipField(charSerialize, ref pos, wire)) break;
        }
        if (equipList.IsEmpty || itemPackage.IsEmpty) return Array.Empty<GearInstance>();

        var slots = new Dictionary<long, (int Slot, int Refine)>(12);
        var enchants = new Dictionary<long, GearEnchant>(4);
        ParseEquipList(equipList, slots, enchants);
        if (slots.Count == 0) return Array.Empty<GearInstance>();

        var result = new List<GearInstance>(slots.Count);
        ParseItemPackage(itemPackage, slots, enchants, result);
        result.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
        return result;
    }

    // EquipList { equip_list(map<i32,EquipInfo>) = 1; equip_enchant(map<i64,EquipEnchantInfo>) = 5 }
    private static void ParseEquipList(
        ReadOnlySpan<byte> equipList,
        Dictionary<long, (int Slot, int Refine)> slots,
        Dictionary<long, GearEnchant> enchants)
    {
        int pos = 0;
        while (pos < equipList.Length)
        {
            if (!WireProtocol.TryReadTag(equipList, ref pos, out var field, out var wire)) break;
            if (wire == 2 && field is 1 or 5)
            {
                if (!WireProtocol.TryReadLengthDelimited(equipList, ref pos, out var entry)) break;
                if (field == 1) ReadEquipSlotMapEntry(entry, slots);
                else ReadEnchantMapEntry(entry, enchants);
            }
            else if (!WireProtocol.SkipField(equipList, ref pos, wire)) break;
        }
    }

    // map entry { key = 1 (slot, ignored — EquipInfo repeats it); value = 2 (EquipInfo) }
    private static void ReadEquipSlotMapEntry(
        ReadOnlySpan<byte> entry, Dictionary<long, (int Slot, int Refine)> slots)
    {
        int pos = 0;
        while (pos < entry.Length)
        {
            if (!WireProtocol.TryReadTag(entry, ref pos, out var field, out var wire)) break;
            if (wire == 2 && field == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(entry, ref pos, out var info)) break;
                ReadEquipInfo(info, slots);
            }
            else if (!WireProtocol.SkipField(entry, ref pos, wire)) break;
        }
    }

    // EquipInfo { equip_slot = 1; item_uuid = 2; equip_slot_refine_level = 3 }
    private static void ReadEquipInfo(
        ReadOnlySpan<byte> info, Dictionary<long, (int Slot, int Refine)> slots)
    {
        int slot = 0, refine = 0;
        long uuid = 0;
        int pos = 0;
        while (pos < info.Length)
        {
            if (!WireProtocol.TryReadTag(info, ref pos, out var field, out var wire)) break;
            if (wire == 0 && WireProtocol.TryReadVarint(info, ref pos, out var v))
            {
                if (field == 1) slot = (int)v;
                else if (field == 2) uuid = unchecked((long)v);
                else if (field == 3) refine = (int)v;
            }
            else if (!WireProtocol.SkipField(info, ref pos, wire)) break;
        }
        if (uuid != 0) slots[uuid] = (slot, refine);
    }

    // map entry { key = 1 (item uuid); value = 2 (EquipEnchantInfo{ type_id = 1; level = 2 }) }
    private static void ReadEnchantMapEntry(ReadOnlySpan<byte> entry, Dictionary<long, GearEnchant> enchants)
    {
        long uuid = 0;
        int typeId = 0, level = 0;
        int pos = 0;
        while (pos < entry.Length)
        {
            if (!WireProtocol.TryReadTag(entry, ref pos, out var field, out var wire)) break;
            if (wire == 0 && field == 1 && WireProtocol.TryReadVarint(entry, ref pos, out var k))
            {
                uuid = unchecked((long)k);
            }
            else if (wire == 2 && field == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(entry, ref pos, out var info)) break;
                ReadEnchantInfo(info, ref typeId, ref level);
            }
            else if (!WireProtocol.SkipField(entry, ref pos, wire)) break;
        }
        if (uuid != 0 && typeId != 0) enchants[uuid] = new GearEnchant(typeId, level);
    }

    private static void ReadEnchantInfo(ReadOnlySpan<byte> info, ref int typeId, ref int level)
    {
        int pos = 0;
        while (pos < info.Length)
        {
            if (!WireProtocol.TryReadTag(info, ref pos, out var field, out var wire)) break;
            if (wire == 0 && WireProtocol.TryReadVarint(info, ref pos, out var v))
            {
                if (field == 1) typeId = (int)v;
                else if (field == 2) level = (int)v;
            }
            else if (!WireProtocol.SkipField(info, ref pos, wire)) break;
        }
    }

    // ItemPackage { packages(map<i32,Package>) = 1 }
    private static void ParseItemPackage(
        ReadOnlySpan<byte> itemPackage,
        Dictionary<long, (int Slot, int Refine)> slots,
        Dictionary<long, GearEnchant> enchants,
        List<GearInstance> result)
    {
        int pos = 0;
        while (pos < itemPackage.Length)
        {
            if (!WireProtocol.TryReadTag(itemPackage, ref pos, out var field, out var wire)) break;
            if (wire == 2 && field == 1)
            {
                if (!WireProtocol.TryReadLengthDelimited(itemPackage, ref pos, out var entry)) break;
                ReadPackageMapEntry(entry, slots, enchants, result);
            }
            else if (!WireProtocol.SkipField(itemPackage, ref pos, wire)) break;
        }
    }

    // map entry { key = 1 (package type); value = 2 (Package) } — only PackageEquip is parsed.
    private static void ReadPackageMapEntry(
        ReadOnlySpan<byte> entry,
        Dictionary<long, (int Slot, int Refine)> slots,
        Dictionary<long, GearEnchant> enchants,
        List<GearInstance> result)
    {
        long key = -1;
        ReadOnlySpan<byte> package = default;
        int pos = 0;
        while (pos < entry.Length)
        {
            if (!WireProtocol.TryReadTag(entry, ref pos, out var field, out var wire)) break;
            if (wire == 0 && field == 1 && WireProtocol.TryReadVarint(entry, ref pos, out var k))
            {
                key = unchecked((long)k);
            }
            else if (wire == 2 && field == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(entry, ref pos, out var inner)) break;
                package = inner;
            }
            else if (!WireProtocol.SkipField(entry, ref pos, wire)) break;
        }
        if (key != PackageEquip || package.IsEmpty) return;

        ParsePackageItems(package, slots, enchants, result);
    }

    // Package { items(map<i64,Item>) = 4 }
    private static void ParsePackageItems(
        ReadOnlySpan<byte> package,
        Dictionary<long, (int Slot, int Refine)> slots,
        Dictionary<long, GearEnchant> enchants,
        List<GearInstance> result)
    {
        int pos = 0;
        while (pos < package.Length)
        {
            if (!WireProtocol.TryReadTag(package, ref pos, out var field, out var wire)) break;
            if (wire == 2 && field == 4)
            {
                if (!WireProtocol.TryReadLengthDelimited(package, ref pos, out var entry)) break;
                ReadItemMapEntry(entry, slots, enchants, result);
            }
            else if (!WireProtocol.SkipField(package, ref pos, wire)) break;
        }
    }

    // map entry { key = 1 (item uuid); value = 2 (Item) } — only equipped uuids are parsed.
    private static void ReadItemMapEntry(
        ReadOnlySpan<byte> entry,
        Dictionary<long, (int Slot, int Refine)> slots,
        Dictionary<long, GearEnchant> enchants,
        List<GearInstance> result)
    {
        long uuid = 0;
        ReadOnlySpan<byte> item = default;
        int pos = 0;
        while (pos < entry.Length)
        {
            if (!WireProtocol.TryReadTag(entry, ref pos, out var field, out var wire)) break;
            if (wire == 0 && field == 1 && WireProtocol.TryReadVarint(entry, ref pos, out var k))
            {
                uuid = unchecked((long)k);
            }
            else if (wire == 2 && field == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(entry, ref pos, out var inner)) break;
                item = inner;
            }
            else if (!WireProtocol.SkipField(entry, ref pos, wire)) break;
        }
        if (uuid == 0 || item.IsEmpty) return;
        if (!slots.TryGetValue(uuid, out var se)) return;

        GearEnchant? enchant = enchants.TryGetValue(uuid, out var e) ? e : null;
        result.Add(ReadItem(item, uuid, se.Slot, se.Refine, enchant));
    }

    // Item { uuid = 1; config_id = 2; quality = 9; equip_attr = 10 }
    private static GearInstance ReadItem(
        ReadOnlySpan<byte> item, long uuid, int slot, int refine, GearEnchant? enchant)
    {
        int configId = 0, quality = 0;
        ReadOnlySpan<byte> equipAttr = default;
        int pos = 0;
        while (pos < item.Length)
        {
            if (!WireProtocol.TryReadTag(item, ref pos, out var field, out var wire)) break;
            if (wire == 0 && WireProtocol.TryReadVarint(item, ref pos, out var v))
            {
                if (field == 2) configId = (int)v;
                else if (field == 9) quality = (int)v;
            }
            else if (wire == 2 && field == 10)
            {
                if (!WireProtocol.TryReadLengthDelimited(item, ref pos, out var inner)) break;
                equipAttr = inner;
            }
            else if (!WireProtocol.SkipField(item, ref pos, wire)) break;
        }

        var (perfection, attrs) = ReadEquipAttr(equipAttr);
        return new GearInstance(slot, uuid, configId, quality, refine, perfection, attrs, enchant);
    }

    // EquipAttr { perfection_value = 7; basic_attr = 10; advance_attr = 11; recast_attr = 12;
    //             perfection_level = 13; rare_quality_attr = 14; max_perfection_value = 15;
    //             equip_attr_set = 17 }
    // SPEC/school (v2) gear leaves the top-level advance_attr(11) EMPTY and stores the CURRENT spec's
    // rolls in equip_attr_set(17) { basic=1; advance=2; recast=3; rare=4 } instead (in-world 2026-06-13:
    // self raid gear had adv=0 at the top level — the Crit/Luck rolls the game shows live in the set).
    // Per bucket, the set wins when non-empty; else the top-level fills in (normal v1 gear has no set).
    private static (GearPerfection Perfection, GearAttrRolls Attrs) ReadEquipAttr(ReadOnlySpan<byte> equipAttr)
    {
        if (equipAttr.IsEmpty) return (default, GearAttrRolls.Empty);

        int pv = 0, pMax = 0, pLevel = 0;
        List<GearAttrRoll>? basic = null, advanced = null, recast = null, rare = null;
        List<GearAttrRoll>? sBasic = null, sAdvanced = null, sRecast = null, sRare = null;
        int pos = 0;
        while (pos < equipAttr.Length)
        {
            if (!WireProtocol.TryReadTag(equipAttr, ref pos, out var field, out var wire)) break;
            if (wire == 0 && WireProtocol.TryReadVarint(equipAttr, ref pos, out var v))
            {
                if (field == 7) pv = (int)v;
                else if (field == 13) pLevel = (int)v;
                else if (field == 15) pMax = (int)v;
            }
            else if (wire == 2 && field is 10 or 11 or 12 or 14)
            {
                if (!WireProtocol.TryReadLengthDelimited(equipAttr, ref pos, out var entry)) break;
                if (!TryReadAttrMapEntry(entry, school: false, out var roll)) continue;
                var list = field switch
                {
                    10 => basic ??= new List<GearAttrRoll>(4),
                    11 => advanced ??= new List<GearAttrRoll>(4),
                    12 => recast ??= new List<GearAttrRoll>(2),
                    _  => rare ??= new List<GearAttrRoll>(2),
                };
                list.Add(roll);
            }
            else if (wire == 2 && field == 17)
            {
                if (!WireProtocol.TryReadLengthDelimited(equipAttr, ref pos, out var set)) break;
                ReadEquipAttrSet(set, ref sBasic, ref sAdvanced, ref sRecast, ref sRare);
            }
            else if (!WireProtocol.SkipField(equipAttr, ref pos, wire)) break;
        }

        var fBasic    = Pick(sBasic, basic);
        var fAdvanced = Pick(sAdvanced, advanced);
        var fRecast   = Pick(sRecast, recast);
        var fRare     = Pick(sRare, rare);
        var attrs = fBasic.Count == 0 && fAdvanced.Count == 0 && fRecast.Count == 0 && fRare.Count == 0
            ? GearAttrRolls.Empty
            : new GearAttrRolls(fBasic, fAdvanced, fRecast, fRare);
        return (new GearPerfection(pv, pMax, pLevel), attrs);
    }

    private static IReadOnlyList<GearAttrRoll> Pick(List<GearAttrRoll>? set, List<GearAttrRoll>? top)
        => (set is { Count: > 0 } ? set : top) is { } l ? l : Array.Empty<GearAttrRoll>();

    // EquipAttrSet { basic_attr = 1; advance_attr = 2; recast_attr = 3; rare_quality_attr = 4 } — the
    // current spec's roll maps (same {libRowId → percentile} shape as the top-level buckets).
    private static void ReadEquipAttrSet(ReadOnlySpan<byte> set,
        ref List<GearAttrRoll>? basic, ref List<GearAttrRoll>? advanced,
        ref List<GearAttrRoll>? recast, ref List<GearAttrRoll>? rare)
    {
        int pos = 0;
        while (pos < set.Length)
        {
            if (!WireProtocol.TryReadTag(set, ref pos, out var field, out var wire)) break;
            if (wire == 2 && field is 1 or 2 or 3 or 4)
            {
                if (!WireProtocol.TryReadLengthDelimited(set, ref pos, out var entry)) break;
                if (!TryReadAttrMapEntry(entry, school: true, out var roll)) continue;
                var list = field switch
                {
                    1 => basic ??= new List<GearAttrRoll>(4),
                    2 => advanced ??= new List<GearAttrRoll>(4),
                    3 => recast ??= new List<GearAttrRoll>(2),
                    _ => rare ??= new List<GearAttrRoll>(2),
                };
                list.Add(roll);
            }
            else if (!WireProtocol.SkipField(set, ref pos, wire)) break;
        }
    }

    // map<i32,i32> entry { key = 1 (lib-row id); value = 2 (percentile) }. `school` tags the roll's
    // table provenance (top-level buckets = v1 lib; equip_attr_set buckets = v2 school lib).
    private static bool TryReadAttrMapEntry(ReadOnlySpan<byte> entry, bool school, out GearAttrRoll roll)
    {
        int attrId = 0, value = 0;
        int pos = 0;
        while (pos < entry.Length)
        {
            if (!WireProtocol.TryReadTag(entry, ref pos, out var field, out var wire)) break;
            if (wire == 0 && WireProtocol.TryReadVarint(entry, ref pos, out var v))
            {
                if (field == 1) attrId = (int)v;
                else if (field == 2) value = unchecked((int)(long)v);
            }
            else if (!WireProtocol.SkipField(entry, ref pos, wire)) break;
        }
        roll = new GearAttrRoll(attrId, value, school);
        return attrId != 0;
    }
}
