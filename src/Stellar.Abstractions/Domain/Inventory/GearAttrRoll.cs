namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>One ROLLED attribute slot on an owned gear piece. The wire stores the roll as an
/// <c>EquipAttrLibTable</c> ROW id plus a 0–100 roll PERCENTILE — not an attribute id/value pair.
/// Resolve via <c>IGameDataEquip.GetAttrLibRow(LibRowId)</c>: each returned entry's displayed stat
/// is <c>floor(Percentile * (Max - Min) / 100 + Min)</c> (the game's own formula in
/// <c>equip_attr_parse_vm.lua</c>; verified against the live gear sheet 2026-06-12).</summary>
/// <param name="LibRowId"><c>EquipAttrLibTable</c> row id (the wire roll-map key).</param>
/// <param name="Percentile">Roll quality 0–100; 100 = the entry's <c>Max</c>.</param>
/// <param name="School">True when the roll came from <c>EquipAttr.equip_attr_set</c> (the current
/// spec's set): its row id keys the v2 SCHOOL lib table (<c>GetSchoolAttrLibRow</c>), not the v1 table.
/// Resolving it against v1 returned a colliding row's wrong attrs (in-world 2026-06-13).</param>
public readonly record struct GearAttrRoll(int LibRowId, int Percentile, bool School = false);
