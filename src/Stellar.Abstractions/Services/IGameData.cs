namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-only lookup over the game's static <c>Panda.Table</c> data. Resolves
/// Bokura.*TableBase row IDs to typed POCO records with display strings pre-resolved
/// through the game's LocalizationMgr.
///
/// Eager batch (Skill, Buff, Profession, Attribute, Item) is loaded inline at
/// HybridCLR-ready; <see cref="IsAvailable"/> becomes true once that completes.
/// 17 deferred tables drain one per Game.Update tick — lookups return null until
/// their table is loaded (typically a few seconds after IsAvailable=true).
///
/// Strings are resolved at cache-build time. Mid-run language switch will not
/// refresh names until next game restart (deferred to Phase 6+).
/// </summary>
public interface IGameData
{
    /// <summary>True once the eager batch has completed.</summary>
    bool IsAvailable { get; }

    /// <summary>Combat sub-table: skills, buffs, damage attributes.</summary>
    IGameDataCombat    Combat    { get; }
    /// <summary>Inventory sub-table: items, equipment, modules.</summary>
    IGameDataInventory Inventory { get; }
    /// <summary>Equip sub-table: gear rows, attr-lib roll ranges, slot labels (deferred-loaded).</summary>
    IGameDataEquip     Equip     { get; }
    /// <summary>World sub-table: maps, scenes, NPCs, monsters.</summary>
    IGameDataWorld     World     { get; }
    /// <summary>Progression sub-table: quests, achievements, titles, activities.</summary>
    IGameDataProgress  Progress  { get; }
}
