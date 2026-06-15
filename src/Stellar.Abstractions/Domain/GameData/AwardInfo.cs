namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for an in-game reward/award item from the game table.</summary>
/// <param name="Id">Game-table award id.</param>
/// <param name="Name">Localised award display name.</param>
/// <param name="IconPath">Addressable path for the award's icon sprite.</param>
/// <param name="Quality">Quality tier (higher = rarer).</param>
public readonly record struct AwardInfo(int Id, string Name, string IconPath, int Quality);
