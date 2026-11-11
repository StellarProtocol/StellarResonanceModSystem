using System;
using UnityEngine;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// TWIN nodes of an allowlist element (<c>NativeUiAllowlistEntry.Twins</c>): siblings the game shows INSTEAD of the
/// element at the same spot — the location bar's <c>group_sceneline_recycle</c>, which the Lua swaps in (CanvasGroup
/// alpha via SetUIVisible) while the line is being recycled (live 2026-11-11: same parent + same localPosition as
/// <c>group_sceneline</c>). They live in the element's OUTER wrapper, so one offset + one scale-0 hide covers both and
/// the user sees one element; the outline is the union of whichever of them is drawing.
/// </summary>
internal sealed partial class PandaHudAdapter
{
    // Resolved once per TryResolve (a resolve is rare: first sight, scene rebuild), never on the 10 Hz path. A twin
    // that isn't found stays null and is skipped everywhere; an inner-wrapped element can't have twins (its wrapper
    // lives under the element, so a sibling can't join it).
    private RectTransform[]? ResolveTwins(RectTransform rt, string[]? names)
    {
        if (names == null || names.Length == 0) return null;
        var parent = rt.parent;
        if (parent == null) return null;
        var twins = new RectTransform[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            twins[i] = parent.Find(names[i])?.TryCast<RectTransform>()!;
            if (twins[i] == null) _log.Warning($"[NativeUi] twin '{names[i]}' of '{rt.name}' not found under its parent");
        }
        return twins;
    }

    private static bool AnyTwinActive(ResolvedEntry e)
    {
        if (e.Twins == null) return false;
        foreach (var t in e.Twins)
            if (t != null && t.gameObject.activeInHierarchy) return true;
        return false;
    }

    // Union the element's outline with each DRAWING twin (active, group alpha shown, its "*" union framed something),
    // measured with the same RectChild spec. If the element itself isn't drawing (the game faded it to show the twin),
    // the twin's rect replaces it — so the outline never stretches over an invisible node.
    private static void MergeTwinOutlines(ResolvedEntry e, ref WindowRect rect, ref bool hasContent, ref float alpha)
    {
        var primaryOk = hasContent && alpha >= MinShowingAlpha && e.RectTransform!.gameObject.activeInHierarchy;
        foreach (var t in e.Twins!)
        {
            if (t == null || !t.gameObject.activeInHierarchy) continue;
            var ta = GroupAlpha(t);
            if (ta < MinShowingAlpha) continue;
            var r = ComputeOutlineRect(t, e.Camera, e.RectChildPath, out var hc);
            if (!hc) continue;
            if (!primaryOk) { rect = r; alpha = ta; hasContent = true; primaryOk = true; continue; }
            float x = Math.Min(rect.X, r.X), y = Math.Min(rect.Y, r.Y);
            rect = new WindowRect(x, y, Math.Max(rect.Right, r.Right) - x, Math.Max(rect.Bottom, r.Bottom) - y);
            alpha = Math.Max(alpha, ta);
        }
    }

    // Outer wrapper intact = every live twin that is still in the element's original parent sits under our wrapper
    // (IsChildOf: value result, no proxy allocation on the 10 Hz check). A twin the game put back beside the wrapper →
    // not intact → re-wrap, which re-adopts it. A twin moved out of that parent altogether is ignored (re-wrapping
    // could never adopt it, so failing on it would re-wrap every tick).
    private static bool TwinsUnder(ResolvedEntry e, RectTransform w)
    {
        if (e.Twins == null || e.TwinSpace == null) return true;
        foreach (var t in e.Twins)
            if (t != null && !t.IsChildOf(w) && t.IsChildOf(e.TwinSpace)) return false;
        return true;
    }

    // Move each twin still beside the element into the freshly inserted outer wrapper, keeping their relative sibling
    // order: a twin that sat BEFORE the element goes in front of it, one after it behind. elementIdx = the element's
    // sibling index before wrapping (the wrapper now holds it). Allocates nothing; runs only on (re)wrap.
    private static void AdoptTwins(ResolvedEntry e, RectTransform w, Transform space, int elementIdx)
    {
        if (e.Twins == null) return;
        e.TwinSpace = space;
        var rt = e.RectTransform!;
        foreach (var t in e.Twins)
        {
            if (t == null) continue;
            var tp = t.parent;
            if (tp == null || tp.Pointer != space.Pointer) continue;   // not beside the element any more
            var before = t.GetSiblingIndex() < elementIdx;
            t.SetParent(w, false);
            if (before) t.SetSiblingIndex(rt.GetSiblingIndex());
        }
    }
}
