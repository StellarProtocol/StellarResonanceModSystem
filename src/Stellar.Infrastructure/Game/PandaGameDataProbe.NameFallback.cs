using System.Collections.Generic;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    /// <summary>
    /// Locale-gated fallback for a skill/buff row whose localized <c>Name</c> is
    /// empty. Recon (ILSpy of <c>Bokura.BuffTableBase</c> / <c>SkillTableBase</c>)
    /// confirmed <c>get_Name</c> already returns the resolved current-language
    /// string — when it is empty the game genuinely has no localized name for that
    /// row (internal proc-buffs, cooldown trackers, system skills). Only the
    /// design label <c>NameDesign</c> is populated, and it is authored in Chinese.
    ///
    /// <para>
    /// Therefore:
    /// <list type="bullet">
    ///   <item>On a Chinese (design-language) client, fall back to <c>NameDesign</c>
    ///         — it is in the user's language.</item>
    ///   <item>On any non-Chinese client (e.g. English), <c>NameDesign</c> would
    ///         surface Chinese text, so it is NOT used. Instead a curated English
    ///         override is applied (keyed by the design label), and rows with no
    ///         override fall back to an id-based label (<c>Buff &lt;id&gt;</c> /
    ///         <c>Skill &lt;id&gt;</c>) — never Chinese.</item>
    /// </list>
    /// </para>
    ///
    /// The override map is intentionally bounded — it covers the internal procs
    /// users actually see in the CombatMeter breakdown / CooldownBar and is meant
    /// to grow as more surface in the diagnostics log. It is not an attempt to
    /// translate every internal row.
    /// </summary>
    /// <param name="kind">Label prefix for the id-based fallback ("Buff" / "Skill").</param>
    /// <param name="id">Row id, used for the id-based fallback.</param>
    /// <param name="nameDesign">The row's <c>NameDesign</c> design label (may be empty).</param>
    private string ResolveEmptyName(string kind, int id, string? nameDesign)
    {
        if (_clientLanguage.IsDesignLanguage)
        {
            // Chinese client: the design label is in the user's language.
            return string.IsNullOrEmpty(nameDesign) ? $"{kind} {id}" : nameDesign;
        }

        // Non-Chinese client: never surface the Chinese design label.
        if (!string.IsNullOrEmpty(nameDesign) &&
            DesignLabelEnglishOverrides.TryGetValue(nameDesign, out var english))
        {
            return english;
        }

        return $"{kind} {id}";
    }

    /// <summary>
    /// Curated design-label → English overrides for internal procs / system rows
    /// that have no localized <c>Name</c>. Keyed by the Chinese <c>NameDesign</c>
    /// design label. Growable — add entries as new untranslated rows surface in
    /// the CombatMeter / CooldownBar (the diagnostics log dumps each row's name).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> DesignLabelEnglishOverrides =
        new Dictionary<string, string>
        {
            // Lance (法杖) attribute procs — seen in CombatMeter skill breakdown.
            ["法杖-幸运"] = "Lance - Luck",
            ["法杖-智力"] = "Lance - Intelligence",
            ["法杖-力量"] = "Lance - Strength",
            ["法杖-体质"] = "Lance - Constitution",
            ["法杖-敏捷"] = "Lance - Agility",
            ["法杖-精神"] = "Lance - Spirit",
            // Talent (天赋) procs.
            ["天赋-冰霜彗星"] = "Talent - Frost Comet",
            // Internal combat procs / system buffs seen in the CombatMeter breakdown.
            ["气刃突刺计数"] = "Blade Thrust Counter",
            ["虚拟体冰爆炸"] = "Hologram Ice Burst",
            ["羽爆 aoe子buff"] = "Feather Burst (AoE sub-buff)",
            // CooldownBar buffs / cooldown-tracker labels seen in screenshot 2.
            ["时驻秘药"] = "Time-Stasis Elixir",
            ["多重打击"] = "Multi-Strike",
            ["连携"] = "Chain",
            // G-series labels are internal cooldown / stability tracker rows;
            // translate the literal stem and keep the G-tier suffix verbatim.
            ["稳态G6"] = "Stability G6",
            ["极性G6"] = "Polarity G6",
            ["冰魔G1"] = "Frost Fiend G1",
            // Misc internal/proc buffs.
            ["1板-羁绊"] = "Plate 1 - Bond",
            ["水球携带BUFF"] = "Water Orb Carrier Buff",
        };
}
