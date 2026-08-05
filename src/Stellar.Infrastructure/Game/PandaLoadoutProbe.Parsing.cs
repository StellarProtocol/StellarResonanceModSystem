using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Pure row-parsing helpers for <see cref="PandaLoadoutProbe"/> — no Lua bridge / IL2CPP, directly
/// unit-testable. Splits the <c>_StellarLoadoutData</c> global the refresh chunk serializes into
/// <see cref="ParsedPlan"/>s (+ the current id); the per-class resolve + live overlay live in the main
/// partial.
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    // Pure row parser — internal (not private) so it's directly unit-testable without the Lua bridge.
    // First line is "CUR=<int>"; each subsequent row is
    // "<planId>\t<name>\t<professionId>\t<talentStageId>\t<talentNodeIds csv>\t<equip slot:uuid csv>\t<mod slot:uuid csv>".
    // The "LIVE\t…" overlay row is ignored here (its "LIVE" first column fails the int-parse) — the instance
    // ParseLiveLine handles it. Tolerates the OLD 2/4/5-column forms (a stale in-flight read from before an
    // enrichment shipped) — the missing columns simply default to 0/empty, never throw.
    internal static (int? Current, List<ParsedPlan> Plans) ParseLoadoutData(string raw)
    {
        int? current = null;
        var plans = new List<ParsedPlan>();
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("CUR=", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                {
                    current = c;
                }
                continue;
            }

            var cols = line.Split('\t');
            if (cols.Length < 2) continue;
            if (!int.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;

            var name = cols[1];
            var professionId = cols.Length > 2
                && int.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prof) ? prof : 0;
            var talentStageId = cols.Length > 3
                && int.TryParse(cols[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stage) ? stage : 0;
            var talentNodes = cols.Length > 4 ? ParseNodeCsv(cols[4]) : null;
            var equipUuids = cols.Length > 5 ? ParseUuidMap(cols[5]) : EmptyUuidMap;
            var modUuids = cols.Length > 6 ? ParseUuidMap(cols[6]) : EmptyUuidMap;

            plans.Add(new ParsedPlan(id, name.Length == 0 ? $"Loadout {id}" : name,
                professionId, talentStageId, talentNodes, equipUuids, modUuids));
        }

        // Sort by planId so hotkey N → a deterministic loadout. PlanDataDict is a Lua
        // map (pairs order is unspecified, and planIds go sparse after delete/recreate),
        // so without this the hotkey→loadout mapping is unstable across sessions.
        plans.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return (current, plans);
    }

    /// <summary>The CURRENT class's live equipped set + talents, parsed from the refresh chunk's
    /// "LIVE\t&lt;equip&gt;\t&lt;mod&gt;\t&lt;curProf&gt;\t&lt;talentStage&gt;\t&lt;talentNodes&gt;" row. This is the
    /// live source for the class the player is actively using — and, when that class has NO saved plan,
    /// the ONLY source of its loadout (owner requirement 2026-08-05: capture what's currently equipped,
    /// not the saved loadout).</summary>
    internal readonly record struct LiveLoadout(
        IReadOnlyDictionary<int, long> Equip,
        IReadOnlyDictionary<int, long> Mod,
        int ProfessionId,
        int TalentStageId,
        IReadOnlyList<int>? TalentNodes);

    // Pure LIVE-row parser — internal (not private) so it's directly unit-testable without the Lua bridge.
    // Finds the "LIVE\t…" row and splits it into the current class's equip/mod slot→uuid maps + its
    // profession/talent-stage/talent-node-ids. Tolerates the OLD 3-column "LIVE\t<eq>\t<mod>" form (a stale
    // in-flight read from before the talent enrichment shipped): the missing columns default to 0/0/null.
    // No LIVE row → an all-empty LiveLoadout (Equip/Mod = the shared empty map, ProfessionId 0, nodes null).
    internal static LiveLoadout ParseLiveLine(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            if (!line.StartsWith("LIVE\t", StringComparison.Ordinal)) continue;
            var cols = line.Split('\t');
            var equip = cols.Length > 1 ? ParseUuidMap(cols[1]) : EmptyUuidMap;
            var mod = cols.Length > 2 ? ParseUuidMap(cols[2]) : EmptyUuidMap;
            var professionId = cols.Length > 3
                && int.TryParse(cols[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var prof) ? prof : 0;
            var talentStageId = cols.Length > 4
                && int.TryParse(cols[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stage) ? stage : 0;
            var talentNodes = cols.Length > 5 ? ParseNodeCsv(cols[5]) : null;
            return new LiveLoadout(equip, mod, professionId, talentStageId, talentNodes);
        }
        return new LiveLoadout(EmptyUuidMap, EmptyUuidMap, 0, 0, null);
    }

    private static readonly IReadOnlyDictionary<int, long> EmptyUuidMap = new Dictionary<int, long>(0);

    // Parses a "slot:uuid,slot:uuid" list into a slot→uuid map. Malformed pairs are skipped, never
    // thrown; an empty/absent field yields the shared empty map (no allocation).
    private static IReadOnlyDictionary<int, long> ParseUuidMap(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return EmptyUuidMap;
        Dictionary<int, long>? map = null;
        foreach (var pair in csv.Split(','))
        {
            var colon = pair.IndexOf(':');
            if (colon <= 0 || colon >= pair.Length - 1) continue;
            if (int.TryParse(pair.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)
                && long.TryParse(pair.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var uuid))
            {
                (map ??= new Dictionary<int, long>()).Add(slot, uuid);
            }
        }
        return map ?? EmptyUuidMap;
    }

    // Parse a comma-separated node-id list ("233002,5205,...") into ints; returns null when the
    // field is empty (no allocation captured) so LoadoutEntry.TalentNodes stays null rather than
    // an empty list. Non-numeric parts are skipped, never thrown.
    private static List<int>? ParseNodeCsv(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return null;
        List<int>? nodes = null;
        foreach (var part in csv.Split(','))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                (nodes ??= new List<int>()).Add(n);
            }
        }
        return nodes;
    }
}
