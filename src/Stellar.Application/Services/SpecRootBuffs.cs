using System.Collections.Generic;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Spec-root-buff knowledge (spec-from-talent-buffs, owner go 2026-09-26; evidence
/// <c>docs/recon/spec-from-public-wire-data.md</c>). Each of the 18 specs has exactly ONE root talent
/// ("&lt;Spec&gt; Spec") that grants exactly one spec-exclusive buff; a player carrying that buff IS that spec.
/// The map is derived from the game's own tables (<see cref="Derive"/>); <see cref="Fallback"/> holds the
/// release_3.7 result for when the tables are unavailable or the derivation is not 18 entries.
/// Spec ids use the <c>ProfessionSpecs</c> format <c>WeaponType*10000 + BdType + 1</c>.
/// </summary>
internal static class SpecRootBuffs
{
    /// <summary>The number of specs (9 playable classes × 2).</summary>
    public const int ExpectedCount = 18;

    private const int ExpertiseTwoStage = 1;
    private const int EffectGrantBuff = 3;

    /// <summary>buff id → spec id as derived from release_3.7 tables (pinned by a test).</summary>
    public static readonly IReadOnlyDictionary<int, int> Fallback = new Dictionary<int, int>
    {
        { 2200320, 10001 },  { 2200590, 10002 },    // Stormblade: Iaido / Moonstrike
        { 2204300, 20001 },  { 2204120, 20002 },    // Frost Mage: Icicle / Frostbeam
        { 2208130, 30001 },  { 2208430, 30002 },    // Twin Striker: Formless / Crimson
        { 2205300, 40001 },  { 2205290, 40002 },    // Wind Knight: Vanguard / Skyward
        { 2202110, 50001 },  { 2202340, 50002 },    // Verdant Oracle: Smite / Lifebind
        { 2201330, 90001 },  { 2201320, 90002 },    // Heavy Guardian: Earthfort / Block
        { 2203260, 110001 }, { 2203290, 110002 },   // Marksman: Wildpack / Falconry
        { 2206090, 120001 }, { 2206190, 120002 },   // Shield Knight: Recovery / Shield
        { 2207090, 130001 }, { 2207180, 130002 },   // Beat Performer: Dissonance / Concerto
    };

    /// <summary>Spec id for a profession (<c>WeaponType</c>) and its 0/1 spec slot (<c>BdType</c>).</summary>
    public static int SpecIdFor(int weaponType, int bdType) => weaponType * 10000 + bdType + 1;

    /// <summary>The profession id encoded in a spec id (<c>50002</c> → <c>5</c>).</summary>
    public static int ProfessionOf(int specId) => specId / 10000;

    /// <summary>
    /// <c>TalentStageTable</c> rows with <c>TalentStage == 1</c> → <c>RootId</c> (TalentTreeTable id) → its
    /// <c>TalentId</c> → <c>TalentTable.TalentEffect</c> entry <c>[3, buffId, level]</c>. A stage whose chain
    /// is broken, or whose root talent grants zero or several buffs, is skipped; a buff claimed by two specs
    /// is dropped. The caller checks the count against <see cref="ExpectedCount"/>.
    /// </summary>
    public static Dictionary<int, int> Derive(SpecTalentTables tables)
    {
        var result = new Dictionary<int, int>(ExpectedCount);
        var collided = new HashSet<int>();
        foreach (var stage in tables.Stages.Values)
        {
            if (stage.TalentStage != ExpertiseTwoStage || stage.RootId == 0) continue;
            if (!TryRootBuff(tables, stage.RootId, out var buffId)) continue;
            if (collided.Contains(buffId)) continue;
            if (result.Remove(buffId)) { collided.Add(buffId); continue; }
            result[buffId] = SpecIdFor(stage.WeaponType, stage.BdType);
        }
        return result;
    }

    private static bool TryRootBuff(SpecTalentTables tables, int rootTreeId, out int buffId)
    {
        buffId = 0;
        if (!tables.TreeTalentIds.TryGetValue(rootTreeId, out var talentId)) return false;
        if (!tables.Talents.TryGetValue(talentId, out var talent) || talent.Effects is null) return false;
        int found = 0;
        foreach (var effect in talent.Effects)
        {
            if (effect is null || effect.Length < 2 || effect[0] != EffectGrantBuff || effect[1] == 0) continue;
            buffId = effect[1];
            found++;
        }
        return found == 1;
    }
}
