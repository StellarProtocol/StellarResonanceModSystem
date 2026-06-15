namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single skill from the game table.</summary>
/// <param name="Id">Game-table skill id.</param>
/// <param name="Name">Localised skill display name.</param>
/// <param name="Description">Localised skill description text.</param>
/// <param name="IconPath">Addressable path for the skill's icon sprite.</param>
/// <param name="Kind">Skill classification (active, passive, etc.).</param>
/// <param name="CooldownMs">Base cooldown duration in milliseconds.</param>
/// <param name="IsAoe">True when this skill hits multiple targets.</param>
public readonly record struct SkillInfo(int Id, string Name, string Description, string IconPath, SkillKind Kind, int CooldownMs, bool IsAoe);
