namespace Stellar.Abstractions.Services;
using Stellar.Abstractions.Domain.GameData;

/// <summary>Static game-data lookups for Battle Imagines (Resonance Skills).</summary>
public interface IGameDataResonance
{
    /// <summary>
    /// Display + cooldown info for an equipped skill id, or null if the skill is
    /// not a Battle Imagine. A skill is a Battle Imagine iff its
    /// <c>SkillTable[skillId].SlotPositionId</c> contains 7 or 8 (the "aoyi" slots).
    /// Results (including negatives) are memoised per skill id.
    /// </summary>
    ImagineInfo? GetImagineForSkill(int skillId);
}
