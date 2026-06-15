namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single achievement entry from the game table.</summary>
/// <param name="Id">Game-table achievement id.</param>
/// <param name="Name">Localised achievement display name.</param>
/// <param name="Description">Localised achievement description.</param>
/// <param name="Points">Achievement point value.</param>
public readonly record struct AchievementInfo(int Id, string Name, string Description, int Points);
