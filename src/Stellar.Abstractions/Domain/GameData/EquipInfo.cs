namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single equipment piece from the game table.</summary>
/// <param name="Id">Game-table equipment id.</param>
/// <param name="Name">Localised equipment display name.</param>
/// <param name="Slot">Equipment slot index.</param>
/// <param name="BaseAttrs">Array of base attribute ids granted by this equipment piece.</param>
public readonly record struct EquipInfo(int Id, string Name, int Slot, int[] BaseAttrs);
