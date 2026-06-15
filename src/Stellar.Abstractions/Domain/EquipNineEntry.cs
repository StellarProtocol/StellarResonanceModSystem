namespace Stellar.Abstractions.Domain;

/// <summary>One equipped slot from the wire's <c>AttrEquipData</c> (<c>EquipNine</c>): which item id
/// occupies which equipment slot. Names/stats are resolved separately from game-data tables.</summary>
public readonly record struct EquipNineEntry(int Slot, int ItemId);
