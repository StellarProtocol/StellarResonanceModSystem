using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.GameData;

/// <summary>
/// Static skill-id → sub-profession (spec) knowledge. There is no wire field for spec — it is
/// derived from the skills a player has equipped (the AOI <c>AttrSkillLevelIdList</c> loadout
/// carries each spec's signature skills, so the spec is known the moment a player appears,
/// before any combat) or, as a fallback, from the skills they cast. Mapping ported from the
/// BPSR-ZDPS reference (<c>Professions.GetSubProfessionIdBySkillId</c>).
///
/// <para>Sub-profession ids use the format <c>&lt;ProfessionId&gt;_00_&lt;SpecIndex&gt;</c> as
/// decimal: 01_00_01 = 10001 (Stormblade / Iaido), 02_00_01 = 20001 (FrostMage / Icicle), etc.</para>
/// </summary>
public static class ProfessionSpecs
{
    // Key = skill id, Value = sub-profession id (decimal digit-separated format above).
    // Skill id 0 and any unmapped id → null (no "Unknown" sentinel returned).
    private static readonly Dictionary<int, int> SkillToSubProfession = new()
    {
        // Stormblade — Iaido (01_00_01 = 10001)
        { 1714,    10001 },
        { 1734,    10001 },

        // Stormblade — Moonstrike (01_00_02 = 10002)
        { 1715,    10002 },
        { 1740,    10002 },
        { 1741,    10002 },
        { 179906,  10002 },

        // FrostMage — Icicle (02_00_01 = 20001)
        { 120901,  20001 },
        { 120902,  20001 },

        // FrostMage — Frostbeam (02_00_02 = 20002)
        { 1241,    20002 },

        // TwinStriker — Formless Expertise (03_00_01 = 30001)
        { 35107,   30001 },
        { 35108,   30001 },
        { 35109,   30001 },
        { 160102,  30001 },

        // TwinStriker — Crimson Expertise (03_00_02 = 30002)
        { 1606,    30002 },
        { 1621,    30002 },
        { 1622,    30002 },
        { 1623,    30002 },

        // WindKnight — Vanguard (04_00_01 = 40001)
        { 1405,    40001 },
        { 1418,    40001 },

        // WindKnight — Skyward (04_00_02 = 40002)
        { 1419,    40002 },

        // VerdantOracle — Smite (05_00_01 = 50001)
        { 1518,    50001 },
        { 1541,    50001 },
        { 21402,   50001 },

        // VerdantOracle — Lifebind (05_00_02 = 50002)
        { 20301,   50002 },

        // HeavyGuardian — Earthfort (09_00_01 = 90001)
        { 199902,  90001 },

        // HeavyGuardian — Block (09_00_02 = 90002)
        { 1930,    90002 },
        { 1931,    90002 },
        { 1934,    90002 },
        { 1935,    90002 },

        // Marksman — Wildpack (11_00_01 = 110001)
        { 2292,    110001 },
        { 1700820, 110001 },
        { 1700825, 110001 },
        { 1700827, 110001 },

        // Marksman — Falconry (11_00_02 = 110002)
        { 220112,  110002 },
        { 2203622, 110002 },
        { 220106,  110002 },

        // ShieldKnight — Recovery (12_00_01 = 120001)
        { 2405,    120001 },

        // ShieldKnight — Shield (12_00_02 = 120002)
        { 2406,    120002 },

        // BeatPerformer — Dissonance (13_00_01 = 130001)
        { 2321,    130001 },
        { 2335,    130001 },

        // BeatPerformer — Concerto (13_00_02 = 130002)
        { 2301,    130002 },
        { 2336,    130002 },
        { 2361,    130002 },
        { 55302,   130002 },
    };

    // English display names inline — no localization layer in Stellar.
    private static readonly Dictionary<int, string> SubProfessionNames = new()
    {
        { 10001, "Iaido" },
        { 10002, "Moonstrike" },
        { 20001, "Icicle" },
        { 20002, "Frostbeam" },
        { 30001, "Formless Expertise" },
        { 30002, "Crimson Expertise" },
        { 40001, "Vanguard" },
        { 40002, "Skyward" },
        { 50001, "Smite" },
        { 50002, "Lifebind" },
        { 90001, "Earthfort" },
        { 90002, "Block" },
        { 110001, "Wildpack" },
        { 110002, "Falconry" },
        { 120001, "Recovery" },
        { 120002, "Shield" },
        { 130001, "Dissonance" },
        { 130002, "Concerto" },
    };

    /// <summary>
    /// Returns the sub-profession id for the given skill id, or <c>null</c> if the
    /// skill id is not associated with any known spec (including skill id 0).
    /// </summary>
    public static int? SubProfessionFromSkill(int skillId)
        => SkillToSubProfession.TryGetValue(skillId, out var id) ? id : null;

    /// <summary>
    /// Resolves the spec from an equipped-skill loadout (first signature-skill match), or
    /// <c>null</c> when no loadout skill maps to a spec. Pre-combat: works the moment the AOI
    /// broadcast delivers the loadout.
    /// </summary>
    public static int? FromLoadout(IReadOnlyList<SkillLevel> loadout)
    {
        for (var i = 0; i < loadout.Count; i++)
            if (SubProfessionFromSkill(loadout[i].SkillId) is { } id) return id;
        return null;
    }

    /// <summary>
    /// Returns the English display name for the given sub-profession id, or
    /// <c>null</c> if the id is not recognised (including 0 / Unknown).
    /// </summary>
    public static string? Name(int subProfessionId)
        => SubProfessionNames.TryGetValue(subProfessionId, out var name) ? name : null;

    // sub-profession id → talent-school id (ETalentId, ZDPS-ported). The school-lib gear table keys its
    // spec-dependent advanced rolls by these ids (EquipAttrSchoolLib.TalentSchoolId), so resolving a
    // far player's spec from their AOI loadout lets us show the right roll ranges for raid (v2) gear.
    private static readonly Dictionary<int, int> SubProfessionToTalentSchool = new()
    {
        { 10001, 101 }, { 10002, 102 },          // Stormblade: Iaido / Moonstrike
        { 20001, 104 }, { 20002, 105 },          // FrostMage: Icicle / Frostbeam
        { 30001, 124 }, { 30002, 125 },          // TwinStriker: Formless / Crimson
        { 40001, 107 }, { 40002, 108 },          // WindKnight: Vanguard / Skyward
        { 50001, 110 }, { 50002, 111 },          // VerdantOracle: Smite / Lifebind
        { 90001, 113 }, { 90002, 114 },          // HeavyGuardian: Earthfort / Block
        { 110001, 116 }, { 110002, 117 },        // Marksman: Wildpack / Falconry
        { 120001, 122 }, { 120002, 123 },        // ShieldKnight: Recovery / Shield
        { 130001, 119 }, { 130002, 120 },        // BeatPerformer: Dissonance / Concerto
    };

    /// <summary>
    /// Resolves the talent-school id (for school-lib gear-attr lookups) from a player's SPEC. Returns 0
    /// when the spec is unknown — the school gear-attr table is keyed by spec ids (104=Icicle …), NOT
    /// base-profession ids, so a base-profession fallback matched nothing and produced a silently-empty
    /// Advanced section (in-world 2026-06-13). 0 lets callers show an honest "spec unknown" message.
    /// </summary>
    public static int TalentSchool(int subProfessionId)
        => SubProfessionToTalentSchool.TryGetValue(subProfessionId, out var t) ? t : 0;
}
