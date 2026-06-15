namespace Stellar.Abstractions.Domain.GameData;

/// <summary>One attribute entry of an equip attr-lib row (<c>Bokura.EquipAttrLibTableBase</c>):
/// the attribute id plus its rolled value range. For BASIC libs min equals max (a fixed value);
/// for ADVANCED libs the range is the roll space shown to inspecting players.</summary>
/// <param name="AttrId">EAttrType id (resolve display via <c>IGameDataCombat.GetAttribute</c>).</param>
/// <param name="Min">Minimum rolled value (== <paramref name="Max"/> for fixed basics).</param>
/// <param name="Max">Maximum rolled value.</param>
public readonly record struct EquipAttrRange(int AttrId, int Min, int Max);
