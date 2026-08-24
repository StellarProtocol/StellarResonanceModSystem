using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.Application.Services;

/// <summary>Pure diff from the live <see cref="DeepSlumberState"/> to a target
/// <see cref="DeepSlumberSetup"/>: the ordered primitive writes that make the live line + factors
/// match the target. Enable-line ops precede that area's factor ops (so the most impactful change
/// lands first); factor ops are emitted in ascending nodeId for determinism. Areas/nodes not named
/// by the target are left alone. No game contact — fully unit-tested.</summary>
internal static class DeepSlumberReconciler
{
    public static IReadOnlyList<DeepSlumberOp> Plan(DeepSlumberState current, DeepSlumberSetup target)
    {
        var ops = new List<DeepSlumberOp>();
        // Match target areas against the CURRENT season ONLY. Season-talent AreaIds are REUSED across
        // seasons — the live container carries every season the character ever touched — so an
        // AreaId-only match can diff a current-season area against a PRIOR season's same-numbered area
        // and emit ops against factors the game's current area never had (owner smoke 2026-08-24: 18
        // bogus UnInstallItemToMiddleNode, every one code 7555 → "partly applied"). The current season
        // is the newest line id present; the plugin captures the same way (logs-site current-season model).
        var currentLine = current.Lines.Count == 0 ? int.MinValue : current.Lines.Max(l => l.LineId);
        foreach (var area in target.Areas.OrderBy(a => a.AreaId))
        {
            var live = FindArea(current, currentLine, area.AreaId);
            if (live is null || !live.IsActive)
                ops.Add(DeepSlumberOp.EnableLine(area.AreaId));

            var liveFactors = ToMap(live?.MiddleNodes);
            var wanted = ToMap(area.Factors);

            // Sockets/replacements for every wanted node.
            foreach (var (node, item) in wanted.OrderBy(kv => kv.Key))
            {
                if (liveFactors.TryGetValue(node, out var cur))
                {
                    if (cur == item) continue;                 // already matches
                    ops.Add(DeepSlumberOp.Unsocket(node, cur)); // replace
                }
                ops.Add(DeepSlumberOp.Socket(node, item));
            }
            // Remove any live factor the target does not name.
            foreach (var (node, cur) in liveFactors.OrderBy(kv => kv.Key))
                if (!wanted.ContainsKey(node))
                    ops.Add(DeepSlumberOp.Unsocket(node, cur));
        }
        return ops;
    }

    private static DeepSlumberArea? FindArea(DeepSlumberState s, int currentLine, int areaId)
    {
        foreach (var line in s.Lines)
        {
            if (line.LineId != currentLine) continue;
            foreach (var a in line.Areas)
                if (a.AreaId == areaId) return a;
        }
        return null;
    }

    private static Dictionary<int, int> ToMap(IReadOnlyList<int[]>? pairs)
    {
        var map = new Dictionary<int, int>();
        if (pairs is null) return map;
        foreach (var p in pairs)
            // itemId 0 = an UNLOCKED-but-EMPTY middle socket, NOT a socketed factor. The live container
            // lists every unlocked socket (empty ones included); treating an empty socket as a factor
            // made the reconciler emit UnInstallItemToMiddleNode against it — which the game rejects
            // (nothing to remove → code 7555 → "partly applied"). Matches the logs site's `itemId !== 0`
            // filter (services/stellar-logs/site/src/lib/deepslumber.ts toAreaVM).
            if (p.Length >= 2 && p[1] != 0) map[p[0]] = p[1];
        return map;
    }
}
