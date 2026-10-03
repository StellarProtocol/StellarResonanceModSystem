using System;
using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>Pure prune decisions for <see cref="GameEffectVisibility"/>'s <c>_instances</c> cache (I1 — the
/// hidden-effects bookkeeping grew without bound: an uid dropped from the ledger as ended was never removed from
/// the instance map, and nothing pruned either collection while the wanted set stayed unchanged). No reflection, no
/// IL2CPP — unit-tested in isolation.</summary>
internal static class InstancePruneRule
{
    /// <summary>Every uid in <paramref name="instanceUids"/> the ledger no longer holds (<paramref name="ledgerContains"/>
    /// false). Called after <c>EffectHideLedger.TakeReleasable</c> runs: it can drop an ended uid from the ledger
    /// without ever showing it back, leaving nothing to remove the matching <c>_instances</c> entry.</summary>
    public static IReadOnlyList<long> NotInLedger(IEnumerable<long> instanceUids, Func<long, bool> ledgerContains)
    {
        var drop = new List<long>();
        foreach (var uid in instanceUids)
            if (!ledgerContains(uid)) drop.Add(uid);
        return drop;
    }

    /// <summary>Bounded prune for the creation-hook path, where Apply (and so <see cref="NotInLedger"/>) may not run
    /// again for a long time while the wanted set stays unchanged: only once <paramref name="count"/> exceeds
    /// <paramref name="threshold"/>, every uid whose <paramref name="state"/> reads <see cref="EffectReleaseState.Ended"/>
    /// (destroyed, or recycled into a different effect). O(n) over the instance map, paid only once the cache has
    /// actually grown past the bound — <paramref name="instanceUids"/> is never enumerated otherwise.</summary>
    public static IReadOnlyList<long> OverBudget(IEnumerable<long> instanceUids, int count, int threshold, Func<long, EffectReleaseState> state)
    {
        if (count <= threshold) return Array.Empty<long>();
        var drop = new List<long>();
        foreach (var uid in instanceUids)
            if (state(uid) == EffectReleaseState.Ended) drop.Add(uid);
        return drop;
    }

    /// <summary>Follow-up (prune hysteresis): the next bounded-prune threshold after a pass, given the instance count
    /// right after that pass. Doubling (never below <paramref name="floor"/>) means a sustained count just above the
    /// floor — hundreds of live effects in a raid, none of them actually ended — pays the O(n) <see cref="OverBudget"/>
    /// pass once instead of on every single creation-hook call.</summary>
    public static int NextThreshold(int countAfterPrune, int floor) => Math.Max(floor, countAfterPrune * 2);
}
