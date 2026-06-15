namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>Socketed enchant on an owned gear piece (wire <c>EquipEnchantInfo</c>,
/// keyed by item uuid in <c>EquipList.equip_enchant</c>).</summary>
/// <param name="ItemTypeId">Config id of the enchant item (joins the item table for display).</param>
/// <param name="Level">Enchant level.</param>
public readonly record struct GearEnchant(int ItemTypeId, int Level);
