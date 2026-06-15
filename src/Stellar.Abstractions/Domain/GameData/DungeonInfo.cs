namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a dungeon / instanced content entry from the game table.</summary>
/// <param name="Id">Game-table dungeon id.</param>
/// <param name="Name">Localised dungeon display name.</param>
/// <param name="MinLevel">Minimum character level required to enter.</param>
/// <param name="Difficulty">Difficulty rating for the dungeon.</param>
public readonly record struct DungeonInfo(int Id, string Name, int MinLevel, int Difficulty);
