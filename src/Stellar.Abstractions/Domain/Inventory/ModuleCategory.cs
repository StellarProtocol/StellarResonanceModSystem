namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>
/// Module category as defined by the game's <c>ModType</c> enum
/// (see <c>lua/ui/model/mod_define.lua: ModType</c>). The game caps the
/// number of equipped modules per category — exceeding the cap triggers
/// a <see cref="EquipResult.SlotConflict"/> on equip.
/// </summary>
public enum ModuleCategory
{
    /// <summary>Offensive attack-enhancing module slot.</summary>
    Attack = 1,
    /// <summary>Utility / support module slot.</summary>
    Assistant = 2,
    /// <summary>Defensive / mitigation module slot.</summary>
    Defend = 3,
}
