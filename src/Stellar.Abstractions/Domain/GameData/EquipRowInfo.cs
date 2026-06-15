namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a gear piece from <c>Bokura.EquipTableBase</c> (the gear-slot table —
/// distinct from <see cref="ItemInfo"/>, which carries the same id's name/quality/icon).</summary>
/// <param name="Id">Equip/item id (shared with <see cref="ItemInfo.Id"/>).</param>
/// <param name="EquipPart">Slot-part code (resolve to a label via <c>IGameDataEquip.GetSlotName</c>).</param>
/// <param name="Gs">Base gear score of the piece.</param>
/// <param name="WearLevel">Required wearing level (0 when the table row carries none).</param>
/// <param name="PerfectCap">Maximum perfection value (0 when none).</param>
/// <param name="BasicLibVersion">Lib-table selector for the basic libs: the FIRST element of the raw
/// table column is a version marker, not a lib id (ZDPS-verified) — 1 = the normal EquipAttrLib table,
/// 2 = the spec/school lib table (not loaded; spec-dependent values).</param>
/// <param name="BasicAttrLibIds">Attr-lib ids for the fixed basic attributes (version marker already
/// stripped; typically one lib per breakthrough tier — index 0 = breakthrough 0).</param>
/// <param name="AdvancedLibVersion">Lib-table selector for the advanced libs (same semantics as
/// <paramref name="BasicLibVersion"/>).</param>
/// <param name="AdvancedAttrLibIds">Attr-lib ids for the rolled advanced-attribute slots (version
/// marker already stripped). Resolve each via <c>IGameDataEquip.GetAttrLib(libId, EquipPart)</c> —
/// a lib id has one row per slot-part group, so the lookup is part-filtered.</param>
public readonly record struct EquipRowInfo(
    int Id, int EquipPart, int Gs, int WearLevel, int PerfectCap,
    int BasicLibVersion, int[] BasicAttrLibIds,
    int AdvancedLibVersion, int[] AdvancedAttrLibIds);
