using System;
using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>The four rolled-attribute groups of an owned gear piece (wire <c>EquipAttr</c>
/// maps <c>basic_attr</c>, <c>advance_attr</c>, <c>recast_attr</c>, <c>rare_quality_attr</c>).
/// Lists are never null; empty groups use shared empty arrays.</summary>
/// <param name="Basic">Fixed basic attributes (white lines).</param>
/// <param name="Advanced">Rolled advanced attributes (green lines).</param>
/// <param name="Recast">Reforged/recast attributes (yellow lines).</param>
/// <param name="Rare">Rare-quality attributes (purple lines).</param>
public sealed record GearAttrRolls(
    IReadOnlyList<GearAttrRoll> Basic,
    IReadOnlyList<GearAttrRoll> Advanced,
    IReadOnlyList<GearAttrRoll> Recast,
    IReadOnlyList<GearAttrRoll> Rare)
{
    /// <summary>Shared all-empty instance (piece carried no <c>equip_attr</c>).</summary>
    public static readonly GearAttrRolls Empty = new(
        Array.Empty<GearAttrRoll>(), Array.Empty<GearAttrRoll>(),
        Array.Empty<GearAttrRoll>(), Array.Empty<GearAttrRoll>());
}
