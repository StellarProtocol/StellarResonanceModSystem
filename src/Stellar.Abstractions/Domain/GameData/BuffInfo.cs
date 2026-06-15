namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for one buff/debuff row (BuffTable).</summary>
/// <param name="Id">Buff base id (BuffTable row id).</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Tooltip description.</param>
/// <param name="IconPath">ZResLoader address for the buff icon (BuffTable.Icon).</param>
/// <param name="Category">Coarse category mapped from BuffType.</param>
/// <param name="IsDebuff">True when the row is a debuff (BuffType == 0).</param>
/// <param name="SkillId">The skill that applies this buff (BuffTable.SkillId); 0 when unknown. Used to attribute
/// an Imagine-lockout debuff back to its source Imagine via <see cref="Services.IGameDataResonance"/>.</param>
public readonly record struct BuffInfo(
    int Id, string Name, string Description, string IconPath,
    BuffCategory Category, bool IsDebuff, int SkillId = 0);
