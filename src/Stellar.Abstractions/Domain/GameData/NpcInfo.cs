namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single NPC from the game table.</summary>
/// <param name="Id">Game-table NPC id.</param>
/// <param name="Name">Localised NPC display name.</param>
/// <param name="Title">Localised NPC title or role string.</param>
/// <param name="FactionId">Faction id this NPC belongs to.</param>
public readonly record struct NpcInfo(int Id, string Name, string Title, int FactionId);
