using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.Application.Services;

/// <summary>Pure diff from the live <see cref="DeepSlumberState"/> to a target
/// <see cref="DeepSlumberSetup"/>: the ordered primitive writes that make the live line + tree + factors
/// match the target. Emitted in Kind order — enable lines, reset differing areas, unsocket, activate the
/// target tree, then socket — so the most impactful change lands first and every load-bearing invariant
/// holds (a line is enabled before its nodes move; a differing tree is reset before it is rebuilt; the
/// tree is rebuilt before its factors socket; scarce factors are freed before any socket needs them).
/// Areas/nodes not named by the target are left alone, except that a wanted scarce factor is freed from
/// a non-target area holding it. No game contact — fully unit-tested.</summary>
internal static class DeepSlumberReconciler
{
    public static IReadOnlyList<DeepSlumberOp> Plan(DeepSlumberState current, DeepSlumberSetup target)
    {
        // Match target areas against the CURRENT season ONLY. Season-talent AreaIds are REUSED across
        // seasons — the live container carries every season the character ever touched — so an
        // AreaId-only match can diff a current-season area against a PRIOR season's same-numbered area
        // and emit ops against factors the game's current area never had (owner smoke: 18 bogus
        // UnInstallItemToMiddleNode, every one code 7555). The current season is the newest line id
        // present; the plugin captures the same way (logs-site current-season model).
        var b = new Buckets();
        var currentLine = current.Lines.Count == 0 ? int.MinValue : current.Lines.Max(l => l.LineId);
        foreach (var area in target.Areas.OrderBy(a => a.AreaId))
            PlanArea(current, currentLine, area, b);
        FreeForeignSharedFactors(current, currentLine, target, b);   // NEW — cross-loadout factor move
        return b.Flatten();
    }

    private static void PlanArea(DeepSlumberState current, int currentLine, DeepSlumberAreaBinding area, Buckets b)
    {
        var live = FindArea(current, currentLine, area.AreaId);
        if (live is null || !live.IsActive)
            b.Enables.Add(DeepSlumberOp.EnableLine(area.AreaId));

        var liveFactors = ToMap(live?.MiddleNodes);
        var wanted = ToMap(area.Factors);

        ReconcileTree(area, live, liveFactors, b);   // may clear liveFactors (a reset frees them all)
        ReconcileFactors(wanted, liveFactors, b);
    }

    // Tree (Anchors of the Mind / normal nodes) reconcile. A null target tree = a legacy binding whose
    // tree was never captured → leave the live tree alone (factor-only, never reset — resetting from an
    // unknown target would nuke the live tree). A non-null (possibly empty) tree is the exact target:
    // reset + rebuild when the live tree differs, since the game has NO per-node anchor removal (owner
    // 2026-09-01) — the only way to remove an anchor is a whole-area ResetAllNodes.
    private static void ReconcileTree(DeepSlumberAreaBinding area, DeepSlumberArea? live, Dictionary<int, int> liveFactors, Buckets b)
    {
        if (area.NormalNodes is null) return;

        var liveAnchors = ToNodeSet(live?.NormalNodes);
        var targetAnchors = new SortedSet<int>(area.NormalNodes);
        if (liveAnchors.SetEquals(targetAnchors)) return;

        // Reset only when the live area has anchors to remove — an empty area has nothing to remove, so a
        // reset would just spend the game's reset currency for nothing. The reset returns every anchor
        // item AND every socketed factor to the bag, so clear the local factor map: after a reset every
        // wanted factor is a plain socket and nothing is unsocketed.
        if (liveAnchors.Count > 0)
        {
            b.Resets.Add(DeepSlumberOp.ResetNodes(area.AreaId));
            liveFactors.Clear();
        }
        foreach (var node in targetAnchors)                 // SortedSet → ascending, deterministic
            b.Activates.Add(DeepSlumberOp.ActivateNode(node));
    }

    private static void ReconcileFactors(Dictionary<int, int> wanted, Dictionary<int, int> liveFactors, Buckets b)
    {
        foreach (var (node, item) in wanted.OrderBy(kv => kv.Key))
        {
            if (liveFactors.TryGetValue(node, out var cur))
            {
                if (cur == item) continue;                       // already matches
                b.Unsockets.Add(DeepSlumberOp.Unsocket(node, cur)); // replace: free the old first
            }
            b.Sockets.Add(DeepSlumberOp.Socket(node, item));
        }
        // Remove any live factor the target does not name.
        foreach (var (node, cur) in liveFactors.OrderBy(kv => kv.Key))
            if (!wanted.ContainsKey(node))
                b.Unsockets.Add(DeepSlumberOp.Unsocket(node, cur));
    }

    // A scarce factor the target wants can still be socketed in a DIFFERENT loadout's area — the game
    // does not auto-move it (a line switch leaves the old area's factors in place), so socketing it
    // into the target fails 7561 ("still socketed elsewhere"): the "Deep-Slumber partly applied" bug
    // (Toir 2026-09-07). Free every WANTED factor from the current-season areas the target does NOT
    // bind, so it is back in the bag before the socket phase. Unsocket is free + non-consuming and
    // needs no active line (owner 2026-09-07). Scope: (a) CURRENT season only — last season reuses
    // AreaIds and its factors must never be touched; (b) areas OUTSIDE the target — the target's own
    // areas are fully reconciled per-area above, so re-touching them here would double-emit an unsocket;
    // (c) items the target actually wants — never disturb the other loadout's non-shared factors. These
    // ops go into the Unsockets bucket, which Flatten emits before every Socket, so no reordering is
    // needed.
    private static void FreeForeignSharedFactors(DeepSlumberState current, int currentLine, DeepSlumberSetup target, Buckets b)
    {
        var wanted = new HashSet<int>();
        var targetAreas = new HashSet<int>();
        foreach (var area in target.Areas)
        {
            targetAreas.Add(area.AreaId);
            foreach (var f in area.Factors)
                if (f.Length >= 2 && f[1] != 0) wanted.Add(f[1]);
        }
        if (wanted.Count == 0) return;

        var freedNodes = new HashSet<(int Area, int Node)>();
        foreach (var line in current.Lines)
        {
            if (line.LineId != currentLine) continue;
            foreach (var area in line.Areas)
            {
                if (targetAreas.Contains(area.AreaId)) continue;   // target's own areas: reconciled per-area
                if (area.MiddleNodes is null) continue;
                foreach (var p in area.MiddleNodes.OrderBy(x => x.Length >= 1 ? x[0] : int.MinValue))
                {
                    if (p.Length < 2 || p[1] == 0) continue;       // empty socket carries itemId 0
                    if (!wanted.Contains(p[1])) continue;          // only move a factor the target needs
                    if (freedNodes.Add((area.AreaId, p[0])))       // one unsocket per (area, node) — a
                                                                    // repeated node id across DIFFERENT
                                                                    // foreign areas must each emit its own
                                                                    // unsocket, never merge
                        b.Unsockets.Add(DeepSlumberOp.Unsocket(p[0], p[1]));
                }
            }
        }
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

    // Live NormalNodes are [nodeId, activeLevel] pairs; the activeLevel is presence-only, so the tree is
    // the SET of node ids. A listed normal node is an active anchor.
    private static SortedSet<int> ToNodeSet(IReadOnlyList<int[]>? pairs)
    {
        var set = new SortedSet<int>();
        if (pairs is null) return set;
        foreach (var p in pairs)
            if (p.Length >= 1) set.Add(p[0]);
        return set;
    }

    // Kind-ordered accumulators — the flat emit order (enables → resets → unsockets → activates →
    // sockets) mirrors DeepSlumberService's phase barrier, which runs one Kind per phase in this order.
    private sealed class Buckets
    {
        public readonly List<DeepSlumberOp> Enables = new();
        public readonly List<DeepSlumberOp> Resets = new();
        public readonly List<DeepSlumberOp> Unsockets = new();
        public readonly List<DeepSlumberOp> Activates = new();
        public readonly List<DeepSlumberOp> Sockets = new();

        public IReadOnlyList<DeepSlumberOp> Flatten()
        {
            var ops = new List<DeepSlumberOp>(
                Enables.Count + Resets.Count + Unsockets.Count + Activates.Count + Sockets.Count);
            ops.AddRange(Enables);
            ops.AddRange(Resets);
            ops.AddRange(Unsockets);
            ops.AddRange(Activates);
            ops.AddRange(Sockets);
            return ops;
        }
    }
}
