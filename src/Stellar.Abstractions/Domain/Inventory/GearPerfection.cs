namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>Perfection state of an owned gear piece (wire <c>EquipAttr</c> fields
/// <c>perfection_value</c>/<c>max_perfection_value</c>/<c>perfection_level</c>).</summary>
/// <param name="Value">Current perfection value of the piece.</param>
/// <param name="Max">Maximum perfection value the piece can reach.</param>
/// <param name="Level">Perfection level.</param>
public readonly record struct GearPerfection(int Value, int Max, int Level);
