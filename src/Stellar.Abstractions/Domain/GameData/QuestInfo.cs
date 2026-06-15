namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single quest from the game table.</summary>
/// <param name="Id">Game-table quest id.</param>
/// <param name="Name">Localised quest display name.</param>
/// <param name="Description">Localised quest description text.</param>
/// <param name="QuestKind">Quest kind integer (main story, side quest, daily, etc.) from the game table.</param>
public readonly record struct QuestInfo(int Id, string Name, string Description, int QuestKind);
