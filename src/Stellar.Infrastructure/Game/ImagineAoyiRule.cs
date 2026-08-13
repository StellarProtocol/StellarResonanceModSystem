using System.Collections.Generic;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Pure resolution rules for Battle-Imagine SUMMON skills. Newer imagines' damage-dealing
/// skills carry <c>SlotPositionId</c> [0]/[6] instead of the aoyi slots 7/8, so the flagged
/// path rejects them and player-attributed summon damage never resolved (verified against
/// prod run jp/RXALtMH6J3 + SkillTable/SkillAoyiTable, 2026-08-13). Two id constructions
/// recover the aoyi identity:
/// <list type="bullet">
///   <item>Monster summon skills are <c>MonsterId*100+NN</c>; <c>SkillAoyiTable[aoyiId].MonsterId</c>
///         closes the loop (e.g. 1008440 → monster 10084 → aoyi 3944 "Arcane! Azure Gale").</item>
///   <item>Companion arcanes live in per-companion 29NNNXX bands with no monster closure;
///         <see cref="MapCompanionArcane"/> carries the curated set.</item>
/// </list>
/// <para>
/// SANCTIONED SYNTHETIC PROBE — NN=00 composites (2026-08-13, buff-only companion capture): a
/// consumer that knows a summon entity's MONSTER CONFIG id but has no skill id at all (the
/// CombatMeter plugin, on <c>CombatEvent.EntitySummonAppeared</c> → <c>GetMonsterByEntity</c>)
/// probes the summon's imagine identity as <c>GetImagineForSkill(configId * 100)</c>. That is a
/// CONSUMER CONTRACT, not an accident of the decomposition, and it rides on three facts:
/// (1) <see cref="CandidateMonsterId"/><c>(configId * 100) == configId</c> — exact division,
/// NN=00; (2) membership of <c>configId</c> in the SkillAoyiTable MonsterId index decides
/// imagine-or-not, exactly as for a real NN&gt;0 summon skill — the column holds BOTH
/// monster-summon ids (10084 Celestial Flier) and companion "- Resonance" ids (3000033 Tina →
/// aoyi 3921), so the one closure covers buff-only companions too; (3) an NN=00 composite can
/// never collide with a leveled PLAYER id — levels start at 1, so no SkillFightLevelTable row
/// exists at <c>*00</c> and <c>GetImagineForSkill</c>'s <c>baseId == 0</c> gate stays open.
/// Overflow: the largest MonsterId band is the 7-digit companion one (3000033 * 100 =
/// 300003300), which fits <c>int</c>; consumers still guard
/// <c>configId &lt;= int.MaxValue / 100</c> before composing. Do NOT repurpose the NN=00 band or
/// tighten <see cref="MinCompositeSkillId"/> past it — the plugin's appear-sourced companion
/// capture depends on the composite resolving (pinned in ImagineAoyiRuleTests).
/// </para>
/// </summary>
internal static class ImagineAoyiRule
{
    // Leveled ids and summon ids both start above this (smallest monster band is 1110 → 111000).
    private const int MinCompositeSkillId = 99_999;

    /// <summary>
    /// The monster id a summon skill id would decompose to (<c>MonsterId*100+NN</c>), or 0 when
    /// the id is too small to hold one. Membership in SkillAoyiTable decides whether the
    /// candidate is real — see <see cref="ResolveSummonAoyi"/>. NN=00 is sanctioned:
    /// <c>CandidateMonsterId(configId * 100) == configId</c> is the round-trip the synthetic
    /// probe contract (class doc) is built on.
    /// </summary>
    public static int CandidateMonsterId(int skillId)
        => skillId > MinCompositeSkillId ? skillId / 100 : 0;

    /// <summary>
    /// Curated companion-arcane → aoyi id map — PENDING TABLE FIX. Older companion arcanes
    /// (2900240/2900340/2900540/2900640) are [7,8]-flagged and resolve via the normal path;
    /// these newer rows are flagged [0]/[6] and have no monster closure (their band 29NNN is
    /// not a SkillAoyiTable MonsterId). Each entry is pinned by SkillTable NameDesign equality
    /// with the aoyi identity row (2900840's differs only by a two-character transposition,
    /// 凭依/依凭). Returns 0 for anything outside the curated set.
    /// </summary>
    public static int MapCompanionArcane(int skillId) => skillId switch
    {
        // Boyce (aoyi 3950, monster 3000045) — "Arcane! Allscape Sublimation", slots [0].
        2900715 or 2900740 => 3950,
        // Rorola (aoyi 3948, monster 3000043) — "Arcane! Divine Assurance", slots [6].
        2900840 => 3948,
        // Fafala (aoyi 3951, monster 3000046) — "Arcane! Guardian's Boundary", slots [0];
        // sibling rows 2900901-2900905 are all "Fafala's …", pinning the band.
        2900940 or 2900942 => 3951,
        _ => 0,
    };

    /// <summary>
    /// Aoyi id for a summon/companion skill (0 = none): curated companion arcanes first, then
    /// the monster-id closure over <paramref name="aoyiByMonster"/> (built from SkillAoyiTable).
    /// The two key sets are disjoint, so the order cannot change a result.
    /// <para>
    /// CALLER CONSTRAINT — the closure cannot see id-namespace collisions: a leveled PLAYER id
    /// (<c>baseSkillId*100+level</c>) can land in a mapped monster band. Measured: 140116 is
    /// both Windborne Grace lv16 (SkillFightLevelTable row, SkillId 1401) and an Igoreus
    /// monster skill (SkillTable "Thunder Vortex"). Callers MUST skip ids that carry a
    /// SkillFightLevelTable row of their own; the leveled-player reading wins there.
    /// </para>
    /// </summary>
    public static int ResolveSummonAoyi(int skillId, IReadOnlyDictionary<int, int> aoyiByMonster)
    {
        int companionAoyi = MapCompanionArcane(skillId);
        if (companionAoyi > 0) return companionAoyi;

        int monsterId = CandidateMonsterId(skillId);
        return monsterId > 0 && aoyiByMonster.TryGetValue(monsterId, out var aoyiId) ? aoyiId : 0;
    }
}
