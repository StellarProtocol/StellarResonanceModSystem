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
        foreach (var area in target.Areas.OrderBy(a => a.AreaId))
        {
            var live = FindArea(current, area.AreaId);
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

    private static DeepSlumberArea? FindArea(DeepSlumberState s, int areaId)
    {
        foreach (var line in s.Lines)
            foreach (var a in line.Areas)
                if (a.AreaId == areaId) return a;
        return null;
    }

    private static Dictionary<int, int> ToMap(IReadOnlyList<int[]>? pairs)
    {
        var map = new Dictionary<int, int>();
        if (pairs is null) return map;
        foreach (var p in pairs)
            if (p.Length >= 2) map[p[0]] = p[1];
        return map;
    }
}
