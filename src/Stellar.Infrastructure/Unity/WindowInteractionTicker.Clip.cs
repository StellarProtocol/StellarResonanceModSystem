using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

// Clip-aware hit-testing for the ticker's MANUAL (RectangleContainsScreenPoint) hit-tests.
//
// A cell scrolled partly/fully out of a ScrollRect viewport is CLIPPED (RectMask2D), not hidden: its rect still
// extends past the viewport edge. The EventSystem path is already safe — RectMask2D and Mask are
// ICanvasRaycastFilters, so Graphic.Raycast rejects a press on the clipped part — but the ticker's geometric tests
// ignore clipping, so the half-hidden last row of a list lit its hover (and took right-clicks / drag presses) while
// the pointer sat BELOW the list over a sibling control (ChatTools whisper list over the composer "+", 2026-10-24).
// The reorder path already handled this with its own viewport check (Reorder.cs: "an item scrolled out of view is
// masked, not hidden"); this is the same rule generalised to every registered cell.
//
// The clip chain (every ancestor RectMask2D / Mask, nearest first) is resolved ONCE per cell and cached by native
// pointer, so the per-frame test is a dictionary hit + one contains-check per clip — no allocation. Prune() clears
// the cache whenever windows are destroyed (a reused native pointer can't pick up a stale chain).
public sealed partial class WindowInteractionTicker
{
    private readonly Dictionary<IntPtr, (Behaviour Clip, RectTransform Rect)[]> _clipChains = new();
    private static readonly (Behaviour Clip, RectTransform Rect)[] NoClips = Array.Empty<(Behaviour, RectTransform)>();

    // True when mp lies outside any enabled ancestor clip of cell — i.e. over a part of the cell the user can't see.
    // Call AFTER the cell's own contains-check passes (the common miss case never pays for the lookup).
    internal bool OutsideClip(RectTransform cell, Vector2 mp)
    {
        var chain = ClipChain(cell);
        for (var i = 0; i < chain.Length; i++)
        {
            var (clip, rect) = chain[i];
            if (clip == null || rect == null || !clip.isActiveAndEnabled) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(rect, mp, null)) return true;
        }
        return false;
    }

    private (Behaviour Clip, RectTransform Rect)[] ClipChain(RectTransform cell)
    {
        var key = cell.Pointer;
        if (_clipChains.TryGetValue(key, out var cached)) return cached;
        List<(Behaviour, RectTransform)>? found = null;
        // Start at the parent: the cell's own rect is already the caller's contains-check (and a RectMask2D ON the
        // cell — e.g. an ellipsized label — only clips its children).
        for (var t = cell.parent; t != null; t = t.parent)
        {
            Behaviour? clip = t.GetComponent<RectMask2D>();
            if (clip == null) clip = t.GetComponent<Mask>();
            if (clip == null) continue;
            // GetComponent, not a C# `is` cast: Transform.parent hands back a Transform-typed interop wrapper, so
            // `is RectTransform` is false even for a RectTransform.
            var rt = t.GetComponent<RectTransform>();
            if (rt != null) (found ??= new()).Add((clip, rt));
        }
        var chain = found == null ? NoClips : found.ToArray();
        _clipChains[key] = chain;
        return chain;
    }
}
