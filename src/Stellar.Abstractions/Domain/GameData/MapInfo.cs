namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single map / zone from the game table.</summary>
/// <param name="Id">Game-table map id.</param>
/// <param name="Name">Localised map display name.</param>
/// <param name="Description">Localised map description text.</param>
/// <param name="IconPath">Addressable path for the map's icon sprite.</param>
public readonly record struct MapInfo(int Id, string Name, string Description, string IconPath);
