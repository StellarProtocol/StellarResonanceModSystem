namespace Stellar.Abstractions.Domain;

/// <summary>
/// One entry of an entity's equipped skill loadout, decoded from the combat
/// wire's <c>AttrSkillLevelIdList</c> attribute (<c>EAttrType</c>=116). The
/// server broadcasts this per-entity for every player in AOI, so it is the
/// canonical source for a player's equipped skills — including Battle Imagines
/// (skills whose <c>SlotPositionId</c> contains 7 or 8).
/// </summary>
/// <param name="SkillId">The equipped skill id (proto <c>skill_id</c>=1).</param>
/// <param name="Level">Current level of the skill (proto <c>current_level</c>=2).</param>
/// <param name="Tier">Remodel level / tier of the skill (proto <c>remodel_level</c>=3).</param>
public readonly record struct SkillLevel(int SkillId, int Level, int Tier);
