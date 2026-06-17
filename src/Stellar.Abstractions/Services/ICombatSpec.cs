using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Per-entity active sub-profession (spec) resolved from observed combat casts.
/// <para>
/// There is no authoritative spec field on the combat wire, and the equipped-skill
/// loadout (<c>AttrSkillLevelIdList</c>) carries both specs' signature skills, so it
/// cannot disambiguate. The framework instead recognises spec-defining skill ids as
/// damage/heal events flow through (last-seen-wins, so a mid-fight spec change is
/// followed) — the same method the ZDPS-family meters use. Resolve the display name
/// via <see cref="Domain.GameData.ProfessionSpecs.Name"/> and the gear talent-school
/// via <see cref="Domain.GameData.ProfessionSpecs.TalentSchool"/>.
/// </para>
/// </summary>
public interface ICombatSpec
{
    /// <summary>
    /// The entity's resolved sub-profession id (e.g. <c>110002</c> = Falconry), or
    /// <c>0</c> if no spec-defining skill has been observed from it yet. Covers any
    /// entity seen casting in AOI this scene — not just party members.
    /// </summary>
    int GetSubProfession(EntityId entityId);
}
