using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Abstractions.Services;

/// <summary>Static-data lookups for the gear-slot tables (equip rows, attr-lib ranges, slot names).
/// Deferred-loaded: lookups return null/empty for a few seconds after boot.</summary>
public interface IGameDataEquip
{
    /// <summary>Returns the gear-piece row for <paramref name="equipId"/>, or null if unknown / not yet loaded.</summary>
    EquipRowInfo? GetEquipRow(int equipId);

    /// <summary>Returns the attribute entries (with value ranges) for an attr-lib id, filtered to the
    /// row whose allow-part list contains <paramref name="equipPart"/> (a lib id has one table row per
    /// slot-part group — an unfiltered merge mixes other slots' ranges in; ZDPS-verified semantics).
    /// Empty list when unknown / not yet loaded.</summary>
    IReadOnlyList<EquipAttrRange> GetAttrLib(int attrLibId, int equipPart);

    /// <summary>Returns the v2 SCHOOL attr-lib entries for an attr-lib id, filtered to the row whose
    /// allow-part list contains <paramref name="equipPart"/> AND whose talent-school list contains
    /// <paramref name="talentSchoolId"/> (resolve via <c>ProfessionSpecs.TalentSchool</c>). This is the
    /// spec-dependent advanced-roll source for raid/v2 gear. Empty when unknown / not yet loaded.</summary>
    IReadOnlyList<EquipAttrRange> GetSchoolAttrLib(int attrLibId, int equipPart, int talentSchoolId);

    /// <summary>Returns the attribute entries for an attr-lib table ROW id, or an empty list. The wire's
    /// per-instance roll maps (<c>EquipAttr.basic_attr</c> etc.) key by row id with a 0–100 roll
    /// percentile value: displayed stat = <c>floor(pct * (Max - Min) / 100 + Min)</c> per entry
    /// (verified against the game's own <c>equip_attr_parse_vm.lua</c>, 2026-06-12).</summary>
    IReadOnlyList<EquipAttrRange> GetAttrLibRow(int rowId);

    /// <summary>Like <see cref="GetAttrLibRow"/> but resolves against the v2 SCHOOL lib table — use for
    /// rolls flagged <see cref="Stellar.Abstractions.Domain.Inventory.GearAttrRoll.School"/> (from
    /// <c>equip_attr_set</c>). Kept separate because a school row id can collide with a v1 row id.</summary>
    IReadOnlyList<EquipAttrRange> GetSchoolAttrLibRow(int rowId);

    /// <summary>Returns the localized slot label for an <see cref="EquipRowInfo.EquipPart"/> code, or null.</summary>
    string? GetSlotName(int equipPart);

    /// <summary>Resolves a socketed gem from the wire's <c>(enchant_item_type_id, enchant_level)</c> pair
    /// to its <see cref="EnchantItemInfo"/> (gem item id whose name carries the display level + flat
    /// effects). Null if unknown / not yet loaded.</summary>
    EnchantItemInfo? GetEnchantItem(int enchantTypeId, int enchantLevel);
}
