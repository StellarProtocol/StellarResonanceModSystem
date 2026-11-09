using System;
using System.Collections.Generic;

namespace Stellar.Application.Services;

/// <summary>
/// Pure order math for the launcher's favorites (2.30.0 drag-to-reorder), kept free of the registry / config so it is
/// unit-testable. Ids are entry titles (the pin identity, see <see cref="LauncherPrefs"/>).
/// <para>The saved order (<c>favorite_order</c>) is only an ORDERING over the pin set: an id in it that is no longer
/// pinned (or not registered this session) is skipped, and a pinned id missing from it — every pin of a config written
/// before reordering existed — follows the ordered ones in registration order. So an existing config keeps exactly
/// the order it showed before (registration order) until the user first reorders or adds a favorite.</para>
/// </summary>
internal static class LauncherFavoriteOrder
{
    /// <summary>The favorites in display order: those in <paramref name="saved"/> first, in that order, then the rest in
    /// their given (registration) order. <paramref name="pinned"/> is every pinned entry in registration order.</summary>
    public static List<T> Sort<T>(IReadOnlyList<T> pinned, Func<T, string> id, IReadOnlyList<string> saved)
    {
        var result = new List<T>(pinned.Count);
        if (saved.Count == 0) { result.AddRange(pinned); return result; }
        var used = new bool[pinned.Count];
        foreach (var s in saved)
            for (var i = 0; i < pinned.Count; i++)
            {
                // First unused match: two registered entries with one title share a pin, keep both, in order.
                if (used[i] || !string.Equals(id(pinned[i]), s, StringComparison.Ordinal)) continue;
                used[i] = true;
                result.Add(pinned[i]);
                break;
            }
        for (var i = 0; i < pinned.Count; i++) if (!used[i]) result.Add(pinned[i]);
        return result;
    }

    /// <summary>Applies a reorder of a SUBSET of the favorites (the ones on screen — a favorite whose plugin hides its
    /// tile right now is not) to the full order: the slots the subset occupies in <paramref name="full"/> are refilled
    /// with <paramref name="subsetInNewOrder"/>, so every other id keeps its exact slot. An id of the subset that is not
    /// in <paramref name="full"/> is appended (a re-starred tile being put back). Duplicate ids are kept once.</summary>
    public static List<string> Apply(IReadOnlyList<string> full, IReadOnlyList<string> subsetInNewOrder)
    {
        var inSubset = new HashSet<string>(subsetInNewOrder, StringComparer.Ordinal);
        var result = new List<string>(full.Count + subsetInNewOrder.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var next = 0;
        foreach (var f in full)
        {
            if (!seen.Add(f)) continue;
            if (!inSubset.Contains(f)) { result.Add(f); continue; }
            // A subset slot: take the next subset id not placed yet.
            while (next < subsetInNewOrder.Count && result.Contains(subsetInNewOrder[next])) next++;
            if (next < subsetInNewOrder.Count) result.Add(subsetInNewOrder[next++]);
        }
        for (; next < subsetInNewOrder.Count; next++)
            if (!result.Contains(subsetInNewOrder[next])) result.Add(subsetInNewOrder[next]);
        return result;
    }

    /// <summary>Remove-then-insert move (the <c>OnReorder(from, to)</c> contract) on a copy of <paramref name="list"/>;
    /// null when either index is out of range.</summary>
    public static List<T>? Move<T>(IReadOnlyList<T> list, int from, int to)
    {
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count) return null;
        var copy = new List<T>(list);
        var x = copy[from];
        copy.RemoveAt(from);
        copy.Insert(to, x);
        return copy;
    }
}
