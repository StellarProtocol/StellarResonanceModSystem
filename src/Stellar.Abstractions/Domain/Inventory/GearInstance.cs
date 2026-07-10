namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>One equipped gear piece of the LOCAL player, decoded from the container sync —
/// per-piece ACTUAL rolls (other players only expose table ranges via the equip tables).</summary>
/// <param name="Slot">Equip slot id (200=weapon … 210=charm).</param>
/// <param name="ItemUuid">Item-instance uuid (joins the enchant/recast maps).</param>
/// <param name="ConfigId">Item/equip config id (joins <see cref="GameData.ItemInfo"/> /
/// <see cref="GameData.EquipRowInfo"/> for name, icon, and roll-space context).</param>
/// <param name="Quality">Item quality tier (1=green … 5=red).</param>
/// <param name="RefineLevel">Refine level of the slot (wire <c>EquipInfo.equip_slot_refine_level</c>).</param>
/// <param name="Perfection">Perfection value/max/level of the piece.</param>
/// <param name="Attrs">The four rolled-attribute groups; never null.</param>
/// <param name="Enchant">Socketed enchant, or null when the piece carries none.</param>
/// <param name="BreakThroughTime">Breakthrough stage (wire <c>EquipAttr.break_through_time</c>);
/// raid gear's displayed item level is the stage's <c>EquipBreakThroughTable.EquipGs</c>.</param>
public sealed record GearInstance(
    int Slot,
    long ItemUuid,
    int ConfigId,
    int Quality,
    int RefineLevel,
    GearPerfection Perfection,
    GearAttrRolls Attrs,
    GearEnchant? Enchant,
    int BreakThroughTime = 0);
