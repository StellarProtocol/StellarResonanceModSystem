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
/// a non-target area holding it — and then ONLY when the target genuinely cannot obtain that factor
/// otherwise (it isn't already socketed in the target, isn't returned by the target's own resets, and
/// no free copy sits in the bag). <paramref name="bagCounts"/> (itemId → free inventory copies, empty
/// when unknown) supplies that last check. No game contact — fully unit-tested.</summary>
internal static class DeepSlumberReconciler
{
    public static IReadOnlyList<DeepSlumberOp> Plan(
        DeepSlumberState current, DeepSlumberSetup target, IReadOnlyDictionary<int, int>? bagCounts = null)
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
        FreeForeignSharedFactors(current, currentLine, target, b, bagCounts);   // cross-loadout factor move
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
            foreach (var item in liveFactors.Values) b.NoteReturnedToBag(item);  // reset refunds every factor
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
    // (Toir 2026-09-07). Free such a factor from the current-season areas the target does NOT bind, so
    // it is back in the bag before the socket phase — but ONLY as many copies as the target genuinely
    // cannot obtain otherwise. A copy is NOT raided from the inactive loadout when the target already
    // holds it, when the target's own reset/replace returns it, or when a spare sits in the bag (Elaina
    // 2026-09-22 — switching to a build whose tree already carries a shared factor stripped it from the
    // other build). Unsocket is free + non-consuming and needs no active line (owner 2026-09-07).
    // Scope: (a) CURRENT season only — last season reuses AreaIds and its factors must never be touched;
    // (b) areas OUTSIDE the target — the target's own areas are fully reconciled per-area above, so
    // re-touching them here would double-emit an unsocket. These ops go into the Unsockets bucket, which
    // Flatten emits before every Socket, so no reordering is needed.
    private static void FreeForeignSharedFactors(
        DeepSlumberState current, int currentLine, DeepSlumberSetup target, Buckets b,
        IReadOnlyDictionary<int, int>? bagCounts)
    {
        var deficit = ComputeBagDeficit(b, bagCounts);
        if (deficit.Count == 0) return;                            // every wanted factor already supplied

        var targetAreas = new HashSet<int>();
        foreach (var area in target.Areas) targetAreas.Add(area.AreaId);

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
                    if (!deficit.TryGetValue(p[1], out var need) || need <= 0) continue; // fully supplied
                    if (freedNodes.Add((area.AreaId, p[0])))       // one unsocket per (area, node) — a
                    {                                               // repeated node id across DIFFERENT
                        b.Unsockets.Add(DeepSlumberOp.Unsocket(p[0], p[1])); // foreign areas each emits its
                        deficit[p[1]] = need - 1;                  // own unsocket; free only up to the deficit
                    }
                }
            }
        }
    }

    // Copies of each wanted factor the target's own sockets still need from a FOREIGN loadout: the count
    // of sockets it emits (each consumes one bag copy) minus every copy already available before the
    // socket phase — the target's own replace/absent unsockets, the factors a reset refunds, and the
    // free copies already in the bag. A positive remainder is a real 7561 risk; zero-or-negative means
    // the factor is already covered and no inactive loadout should be disturbed for it.
    private static Dictionary<int, int> ComputeBagDeficit(Buckets b, IReadOnlyDictionary<int, int>? bagCounts)
    {
        var need = new Dictionary<int, int>();
        foreach (var op in b.Sockets)
            need[op.ItemId] = (need.TryGetValue(op.ItemId, out var c) ? c : 0) + 1;
        if (need.Count == 0) return need;
        foreach (var op in b.Unsockets) Reduce(need, op.CurrentItemId);         // target-area frees
        foreach (var (item, n) in b.ResetFreed) Reduce(need, item, n);          // reset refunds
        if (bagCounts is not null)
            foreach (var (item, n) in bagCounts) Reduce(need, item, n);         // spare copies in the bag
        return need;
    }

    private static void Reduce(Dictionary<int, int> need, int item, int by = 1)
    {
        if (need.TryGetValue(item, out var c)) need[item] = c - by;
    }

    // The REACTIVE-free candidate pool: every current-season foreign-area copy of a factor the target
    // wants, (itemId, node), ordered by (area, node). The predictive pass above already freed the copies
    // the target could not otherwise obtain (inventory-checked); this is the SUPERSET the service draws
    // from when a socket is still refused 7561 (ErrSeasonTalentIntermediateNodeClassNumExceeded — a copy
    // in an inactive tree counts toward the per-type limit, which a bag spare cannot clear). The service
    // skips any (node,item) the predictive pass already unsocketed so it never double-frees.
    internal static IReadOnlyList<(int ItemId, int Node)> ForeignSharedFactorCandidates(
        DeepSlumberState current, DeepSlumberSetup target)
    {
        var wanted = new HashSet<int>();
        var targetAreas = new HashSet<int>();
        foreach (var area in target.Areas)
        {
            targetAreas.Add(area.AreaId);
            foreach (var f in area.Factors)
                if (f.Length >= 2 && f[1] != 0) wanted.Add(f[1]);
        }
        var result = new List<(int, int)>();
        if (wanted.Count == 0) return result;

        var currentLine = current.Lines.Count == 0 ? int.MinValue : current.Lines.Max(l => l.LineId);
        foreach (var line in current.Lines)
        {
            if (line.LineId != currentLine) continue;
            foreach (var area in line.Areas.OrderBy(a => a.AreaId))
            {
                if (targetAreas.Contains(area.AreaId) || area.MiddleNodes is null) continue;
                foreach (var p in area.MiddleNodes.OrderBy(x => x.Length >= 1 ? x[0] : int.MinValue))
                    if (p.Length >= 2 && p[1] != 0 && wanted.Contains(p[1])) result.Add((p[1], p[0]));
            }
        }
        return result;
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

        // itemId → copies a target-area RESET returns to the bag (not emitted as Unsockets — a reset
        // clears its area's factors wholesale). Counts as supply against the foreign-free deficit.
        public readonly Dictionary<int, int> ResetFreed = new();
        public void NoteReturnedToBag(int item) =>
            ResetFreed[item] = ResetFreed.TryGetValue(item, out var c) ? c + 1 : 1;

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
