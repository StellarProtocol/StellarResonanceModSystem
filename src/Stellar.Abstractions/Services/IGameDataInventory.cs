using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Abstractions.Services;

/// <summary>Static-data lookups for inventory-related rows.</summary>
public interface IGameDataInventory
{
    /// <summary>Returns the item row for <paramref name="id"/>, or null if unknown.</summary>
    ItemInfo? GetItem(int id);

    /// <summary>
    /// Returns the equip-enchant-item row for <paramref name="id"/>, or null if
    /// unknown.
    /// <para>
    /// <b>v1 scope warning:</b> on the current build this lookup is backed by
    /// <c>Bokura.EquipEnchantItemTableBase</c> (222 rows of enchant-item
    /// metadata). It does NOT return gear-piece info (helms, gloves, armour
    /// slots, etc.) — those rows live as items in <see cref="GetItem"/> with
    /// <see cref="Stellar.Abstractions.Domain.GameData.ItemKind.Equip"/> /
    /// <see cref="Stellar.Abstractions.Domain.GameData.ItemKind.Module"/>. Calling
    /// <c>GetEquip(helmet_id)</c> will return <c>null</c>. Recon for a separate
    /// gear-slot table is tracked for Phase 7 (<c>ModuleOptimizer</c>); this
    /// signature is preserved so the v2 source can be swapped in without a
    /// breaking change.
    /// </para>
    /// </summary>
    EquipInfo? GetEquip(int id);

    /// <summary>Returns the weapon row for <paramref name="id"/>, or null if unknown.</summary>
    WeaponInfo? GetWeapon(int id);
}
