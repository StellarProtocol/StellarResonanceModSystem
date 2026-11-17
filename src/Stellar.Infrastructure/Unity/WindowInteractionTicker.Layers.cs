using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

// LayerStackElement input blocking for the ticker's GEOMETRIC hit-tests (the uGUI half is the layer's raycast Image —
// WindowBuilder.LayerStack.cs). The ticker tests registered cells by rect, never asking the EventSystem what is on top,
// so without this a blocking "Loading…" overlay would still let the table rows under it light their hover, take
// right-clicks, start a drag-reorder, pan/zoom the chart, and so on.
//
// Rule (LayerStackRules): a cell is unreachable at mp when an ACTIVE blocking layer drawn above the cell's own layer
// contains mp, or when the cell sits inside a non-blocking (click-through) layer. Folded into OutsideClip, which every
// per-cell press/hover hit-test already calls right after its contains-check, so those sites get it for free; the few
// sites that test a list/plot without OutsideClip call LayerCovered directly (Reorder*, ChartNav, ChartPan zoom,
// ScrollViews). Window drag/resize handles are deliberately NOT covered: they are the window frame, not a layer's
// content, and a whole-frame-draggable window still moves when pressed on an overlay (uGUI's topmost hit there is the
// non-Selectable blocker, so WindowDragRule allows it).
//
// Cost: zero when no stack is registered (one Count check). Otherwise the cover of a cell is resolved ONCE (parent walk,
// cached by native pointer like the clip chain) and the per-test work is a dictionary hit + one activeInHierarchy +
// contains-check per blocker — no allocation. Caches are cleared on Prune (window destroy) and on every registration.
public sealed partial class WindowInteractionTicker
{
    private readonly Dictionary<IntPtr, LayerHostInfo<IntPtr, RectTransform>> _layerHosts = new();
    private readonly Dictionary<IntPtr, (GameObject[] Gos, RectTransform[] Rects, bool Inert)> _layerCovers = new();
    private readonly List<RectTransform> _layerHostRects = new();   // for Prune (dead hosts leave the dictionary)
    private static readonly (GameObject[] Gos, RectTransform[] Rects, bool Inert) NoCover =
        (Array.Empty<GameObject>(), Array.Empty<RectTransform>(), false);

    /// <summary>A LayerStack layer host (the stack root for the base, or an overlay wrapper) and the blocking layer rects
    /// drawn above it. Called by WindowBuilder.BuildLayerStack.</summary>
    internal void RegisterLayerHost(RectTransform host, RectTransform stack, RectTransform[] above, bool inert)
    {
        if (host == null || stack == null) return;
        _layerHosts[host.Pointer] = new LayerHostInfo<IntPtr, RectTransform>
        {
            Stack = stack.Pointer, IsOverlay = host.Pointer != stack.Pointer, Inert = inert, Above = above,
        };
        _layerHostRects.Add(host);
        _layerCovers.Clear();   // a cached cell may now sit under this stack (or a recycled pointer was re-registered)
    }

    /// <summary>True when mp, over <paramref name="cell"/>, is covered by a visible blocking layer drawn above the cell's
    /// layer, or the cell is inside a click-through layer. Call after the cell's own contains-check passed.</summary>
    internal bool LayerCovered(RectTransform cell, Vector2 mp)
    {
        if (_layerHosts.Count == 0 || cell == null) return false;
        var c = LayerCover(cell);
        if (c.Inert) return true;
        for (var i = 0; i < c.Rects.Length; i++)
        {
            var go = c.Gos[i];
            if (go == null || !go.activeInHierarchy) continue;   // hidden layer: SetActive(false), blocks nothing
            if (RectTransformUtility.RectangleContainsScreenPoint(c.Rects[i], mp, null)) return true;
        }
        return false;
    }

    private (GameObject[] Gos, RectTransform[] Rects, bool Inert) LayerCover(RectTransform cell)
    {
        var key = cell.Pointer;
        if (_layerCovers.TryGetValue(key, out var cached)) return cached;
        var walk = new LayerCoverWalk<IntPtr, RectTransform>();
        // Start at the cell itself (a registered rect could be a layer host); nearest host first.
        for (Transform? t = cell; t != null; t = t.parent)
            if (_layerHosts.TryGetValue(t.Pointer, out var host)) walk.Visit(host);
        var cover = NoCover;
        if (walk.Blockers != null || walk.Inert)
        {
            var rects = walk.Blockers?.ToArray() ?? Array.Empty<RectTransform>();
            var gos = new GameObject[rects.Length];
            for (var i = 0; i < rects.Length; i++) gos[i] = rects[i] != null ? rects[i].gameObject : null!;
            cover = (gos, rects, walk.Inert);
        }
        _layerCovers[key] = cover;
        return cover;
    }

    private void PruneLayers()
    {
        _layerCovers.Clear();
        for (var i = _layerHostRects.Count - 1; i >= 0; i--)
        {
            if (_layerHostRects[i] != null) continue;
            _layerHostRects.RemoveAt(i);
        }
        // A destroyed host's pointer can't be read back from its dead wrapper, so rebuild the map from the live ones.
        if (_layerHostRects.Count == _layerHosts.Count) return;
        var live = new Dictionary<IntPtr, LayerHostInfo<IntPtr, RectTransform>>(_layerHostRects.Count);
        for (var i = 0; i < _layerHostRects.Count; i++)
            if (_layerHosts.TryGetValue(_layerHostRects[i].Pointer, out var h)) live[_layerHostRects[i].Pointer] = h;
        _layerHosts.Clear();
        foreach (var kv in live) _layerHosts[kv.Key] = kv.Value;
    }
}
