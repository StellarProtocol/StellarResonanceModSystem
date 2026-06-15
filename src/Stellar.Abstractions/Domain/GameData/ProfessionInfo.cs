namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a player profession / class from the game table.</summary>
/// <param name="Id">Game-table profession id.</param>
/// <param name="Name">Localised profession display name.</param>
/// <param name="IconPath">Addressable path for the profession's icon sprite.</param>
/// <param name="HasSecondary">True when this profession has a secondary / sub-profession system.</param>
/// <param name="CommonSkillIds">Skill ids shared across all specialisations of this profession.</param>
public readonly record struct ProfessionInfo(int Id, string Name, string IconPath, bool HasSecondary, int[] CommonSkillIds);
