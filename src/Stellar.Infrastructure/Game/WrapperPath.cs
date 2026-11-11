using System;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// The tree operations <see cref="WrapperPath"/> needs — implemented over Unity Transforms by
/// <c>PandaHudAdapter.UnityTree</c> and over a fake tree in the unit tests. A struct implementation is passed as a
/// generic argument, so the walk makes constrained calls (no boxing, no delegate).
/// </summary>
internal interface IWrapTree<T> where T : class
{
    /// <summary>Direct child by exact name (Transform.Find semantics); null if absent.</summary>
    T? Find(T node, string name);
    /// <summary>Cheap pre-check: can any of OUR wrappers sit below <paramref name="node"/>? false skips the scan.</summary>
    bool MayHoldWrapper(T node);
    int ChildCount(T node);
    T? Child(T node, int index);
    bool IsWrapper(T node);
}

/// <summary>
/// Allowlist path resolution that is TRANSPARENT to the framework's own <c>stellar_wrap_*</c> nodes. Allowlist elements
/// can be NESTED (class gauge <c>…/node_player_state_bar_hide_root/…/group_energy</c> lives inside Player HP/MP
/// <c>…/node_player_state_bar_hide_root</c>): once the outer element is wrapped, the live hierarchy reads
/// <c>…/anim_battle/stellar_wrap_gameui.player-hp/node_player_state_bar_hide_root/…</c>, and a plain segment-by-segment
/// <c>Transform.Find</c> walk breaks at the inserted node — the inner element then never resolved (live 2026-11-11).
/// So: a segment not found directly under a node is looked up under that node's wrapper children (recursively, as
/// wrappers can nest). A direct hit always wins, so behaviour is unchanged wherever nothing of ours is in the way.
/// </summary>
internal static class WrapperPath
{
    public const string Prefix = "stellar_wrap_";

    // Wrappers stack only when elements nest (outer of one + inner of another at the same spot); 4 is generous.
    private const int MaxWrapperDepth = 4;

    public static bool IsWrapperName(string? name) => name != null && name.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Direct child <paramref name="name"/> of <paramref name="node"/>, looking through our wrapper nodes.</summary>
    public static T? FindChild<T, TTree>(TTree tree, T node, string name)
        where T : class where TTree : struct, IWrapTree<T>
    {
        var hit = tree.Find(node, name);
        if (hit != null || !tree.MayHoldWrapper(node)) return hit;   // the common case: one Find, nothing else
        return FindUnderWrappers(tree, node, name, 0);
    }

    /// <summary>Walk <paramref name="segments"/> (pre-split, from index <paramref name="from"/>) below
    /// <paramref name="start"/>; empty and "." segments mean "this node". Null as soon as a segment is missing.</summary>
    public static T? Walk<T, TTree>(TTree tree, T start, string[] segments, int from = 0)
        where T : class where TTree : struct, IWrapTree<T>
    {
        T? cur = start;
        for (var i = from; i < segments.Length && cur != null; i++)
        {
            var seg = segments[i];
            if (seg.Length == 0 || seg == ".") continue;
            cur = FindChild(tree, cur, seg);
        }
        return cur;
    }

    private static T? FindUnderWrappers<T, TTree>(TTree tree, T node, string name, int depth)
        where T : class where TTree : struct, IWrapTree<T>
    {
        if (depth >= MaxWrapperDepth) return null;
        var n = tree.ChildCount(node);
        for (var i = 0; i < n; i++)
        {
            var c = tree.Child(node, i);
            if (c == null || !tree.IsWrapper(c)) continue;
            var hit = tree.Find(c, name) ?? FindUnderWrappers(tree, c, name, depth + 1);
            if (hit != null) return hit;
        }
        return null;
    }
}
