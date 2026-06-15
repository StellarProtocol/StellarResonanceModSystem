namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single talent / passive ability from the game table.</summary>
/// <param name="Id">Game-table talent id.</param>
/// <param name="Name">Localised talent display name.</param>
/// <param name="Description">Localised talent description text.</param>
/// <param name="IconPath">Addressable path for the talent's icon sprite.</param>
/// <param name="ProfessionId">Profession id this talent belongs to.</param>
public readonly record struct TalentInfo(int Id, string Name, string Description, string IconPath, int ProfessionId);
