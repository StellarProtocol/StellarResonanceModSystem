namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single monster type from the game table.</summary>
/// <param name="Id">Game-table monster id.</param>
/// <param name="Name">Localised monster display name.</param>
/// <param name="Level">Base level of this monster type.</param>
/// <param name="FactionId">Faction id this monster belongs to.</param>
/// <param name="IconPath">Addressable path for the monster's icon sprite.</param>
public readonly record struct MonsterInfo(int Id, string Name, int Level, int FactionId, string IconPath);
