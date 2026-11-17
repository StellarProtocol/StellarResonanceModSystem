using System;
using System.Collections.Generic;

namespace Stellar.Infrastructure.Game;

// The Unity-free half of LayerStackElement input blocking (the GameObject side is WindowBuilder.LayerStack.cs, the
// hit-test side WindowInteractionTicker.Layers.cs): which overlay rects cover a given cell. Pure, so it is unit-tested.
//
// A stack registers one "host" per layer: the stack ROOT stands for the base (layer 0 is built straight into it), and
// each overlay wrapper for its overlay. A cell is covered by the BLOCKING overlays drawn above the layer it lives in.
// Walking up from a cell, the first host met is its own layer's. An overlay's cell also has the stack root as an
// ancestor (overlay wrapper → "Overlays" host → stack root), so that root entry — which would apply the base's blockers,
// i.e. the overlay's own — must be skipped once the overlay was met. Hosts further up (an outer stack) still apply.

/// <summary>One registered layer host. <typeparamref name="TKey"/> identifies a stack (the native pointer in game).</summary>
internal sealed class LayerHostInfo<TKey, TBlocker> where TKey : struct, IEquatable<TKey>
{
    public TKey Stack;           // the stack root this layer belongs to
    public bool IsOverlay;       // false = the stack root itself = its base layer
    public bool Inert;           // a non-blocking (click-through) overlay: nothing inside it takes ticker input
    public TBlocker[] Above = Array.Empty<TBlocker>();   // the blocking overlays drawn above this layer
}

/// <summary>Accumulates the cover of one cell while walking from the cell up to the canvas: call <see cref="Visit"/> for
/// every transform that is a registered host (nearest first).</summary>
internal struct LayerCoverWalk<TKey, TBlocker> where TKey : struct, IEquatable<TKey>
{
    private TKey _skipRoot;
    private bool _skipping;
    public List<TBlocker>? Blockers;   // null = covered by nothing
    public bool Inert;

    public void Visit(LayerHostInfo<TKey, TBlocker> host)
    {
        if (!host.IsOverlay && _skipping && host.Stack.Equals(_skipRoot)) { _skipping = false; return; }
        if (host.IsOverlay) { _skipRoot = host.Stack; _skipping = true; if (host.Inert) Inert = true; }
        if (host.Above.Length == 0) return;
        Blockers ??= new List<TBlocker>();
        Blockers.AddRange(host.Above);
    }
}

internal static class LayerStackRules
{
    /// <summary>Indices (into the overlay list) of the blocking overlays drawn above <paramref name="layer"/>
    /// (0 = the base, k = overlay k-1). Overlays draw in list order, so "above" = a later index.</summary>
    public static int[] BlockersAbove(IReadOnlyList<bool> overlayBlocks, int layer)
    {
        var n = 0;
        for (var j = layer; j < overlayBlocks.Count; j++) if (overlayBlocks[j]) n++;
        if (n == 0) return Array.Empty<int>();
        var r = new int[n];
        n = 0;
        for (var j = layer; j < overlayBlocks.Count; j++) if (overlayBlocks[j]) r[n++] = j;
        return r;
    }
}
