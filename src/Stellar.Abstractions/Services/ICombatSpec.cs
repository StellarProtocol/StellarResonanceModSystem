using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Per-entity active sub-profession (spec).
/// <para>
/// <b>Talent-first.</b> Every spec has exactly one root talent ("&lt;Spec&gt; Spec") that grants one
/// spec-exclusive buff, and the game broadcasts each nearby player's buff set (talent buffs included) on
/// AOI appear and on every change. When an entity carries a spec root buff, that spec is AUTHORITATIVE:
/// it is known from the moment the player appears (in town, before any cast) and cast inference cannot
/// overwrite it. During a same-class spec swap the root is removed ~2 s before the new one arrives; the
/// previous spec is held across that gap (no flicker, no 0). If the class (attr 220) changes and no root of
/// the new class is present yet, the old class's spec is not reported. The root-buff map is derived from
/// the game's own talent tables at load (with built-in constants as the fallback).
/// </para>
/// <para>
/// <b>Cast fallback.</b> For an entity whose root buff has never been seen, the spec is recognised from
/// spec-defining skill ids as damage/heal events flow through (last-seen-wins) — the ZDPS-family method. The
/// equipped-skill loadout (<c>AttrSkillLevelIdList</c>) carries both specs' signature skills, so it cannot
/// disambiguate. Use <see cref="TryGetTalentSpec"/> to tell the two sources apart, and
/// <see cref="CombatEvent.SpecChanged"/> to be notified of changes. Resolve the display name via
/// <see cref="Domain.GameData.ProfessionSpecs.Name"/> and the gear talent-school via
/// <see cref="Domain.GameData.ProfessionSpecs.TalentSchool"/>.
/// </para>
/// </summary>
public interface ICombatSpec
{
    /// <summary>
    /// The entity's resolved sub-profession id (e.g. <c>110002</c> = Falconry; format
    /// <c>ProfessionId*10000 + SpecIndex</c>), or <c>0</c> if unknown. Talent-derived when a spec root buff
    /// has been seen for the entity (see the interface remarks), otherwise cast-derived. Covers any entity in
    /// AOI this scene — not just party members.
    /// </summary>
    int GetSubProfession(EntityId entityId);

    /// <summary>
    /// Returns <see langword="true"/> only while the entity's spec is talent-derived: a spec root buff is
    /// present, OR the entity is inside a same-class swap gap and still holds its last talent-derived spec.
    /// Returns <see langword="false"/> when the spec is cast-derived or unknown — including after a class
    /// change with no root buff of the new class yet. Use this when only an authoritative spec is acceptable
    /// (e.g. uploading it); a cast guess never satisfies it.
    /// </summary>
    /// <param name="entityId">The entity to query.</param>
    /// <param name="subProfessionId">The talent-derived sub-profession id when the method returns
    /// <see langword="true"/>; otherwise <c>0</c>.</param>
    bool TryGetTalentSpec(EntityId entityId, out int subProfessionId);
}
