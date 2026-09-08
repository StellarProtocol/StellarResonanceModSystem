using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic-mode rect dump for <see cref="PandaHudAdapter"/>. Gated on
/// <see cref="StellarDiagnostics.IsEnabled"/> so the production path stays free of inline gates
/// (coding-standards § Diagnostics). Triggered from the layout editor on edit-mode enter.
/// </summary>
/// <remarks>
/// The walk mirrors <see cref="AccumulateContentBounds"/> EXACTLY (same depth cap, same inactive-subtree skip,
/// same clip-stop) so the per-child lines show precisely what <see cref="ComputeContentScreenRect"/> unions —
/// letting an oversized outline (bug #5) be traced to the descendant inflating it (typically a full-area
/// transparent / disabled / culled graphic that still carries a CanvasRenderer).
/// </remarks>
internal sealed partial class PandaHudAdapter
{
    private const int DiagMaxLinesPerEntry = 160;   // every active node is logged now (curation needs containers too)

    public void DumpDiagnostics(Action<string> log)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        log("[NativeUi/RectDiag] === resolved native-UI element rects (node vs content vs drawables) ===");
        foreach (var e in _cache.Values)
        {
            if (e.RectTransform == null || e.GameObject == null) continue;
            var node = ToScreenRect(e.RectTransform, e.Camera);
            var content = ComputeContentScreenRect(e.RectTransform, e.Camera);
            var outline = e.OriginalScreenRect;   // what the grab-box ACTUALLY uses (RectChild applied at resolve)
            log($"[NativeUi/RectDiag] {e.Path}");
            log($"[NativeUi/RectDiag]   node    =({node.X:0},{node.Y:0} {node.Width:0}x{node.Height:0}) active={e.GameObject.activeInHierarchy}");
            log($"[NativeUi/RectDiag]   content =({content.X:0},{content.Y:0} {content.Width:0}x{content.Height:0}) (auto)");
            log($"[NativeUi/RectDiag]   OUTLINE =({outline.X:0},{outline.Y:0} {outline.Width:0}x{outline.Height:0}) <- grab-box (RectChild if curated)");
            var count = 0;
            DumpContributors(e.RectTransform.transform, e.Camera, 0, ref count, log);
        }
        log("[NativeUi/RectDiag] === end ===");
    }

    // Mirror of AccumulateContentBounds: visit only what the bounds union visits, logging each contributor's
    // screen rect + why it counts (clip vs drawable) + its graphic visibility (the fields a future filter
    // would key on: enabled / canvasRenderer.cull / colour alpha).
    private void DumpContributors(Transform t, Camera? cam, int depth, ref int count, Action<string> log)
    {
        if (t == null || depth > ContentMaxDepth || count >= DiagMaxLinesPerEntry) return;
        if (!t.gameObject.activeInHierarchy) return;
        var rt = t.TryCast<RectTransform>();
        if (rt == null) return;

        // Log EVERY active node (not just drawables) so a curation target — often a non-drawable layout
        // container whose own rect tightly frames the visible widget — is visible too. Tag: clip / draw
        // (has CanvasRenderer) / node (pure container). The relative path lets the tag be copied straight
        // into an allowlist RectChild.
        var clip = IsClip(t);
        var tag = clip ? "CLIP" : t.GetComponent<CanvasRenderer>() != null ? "draw" : "node";
        var r = ToScreenRect(rt, cam);
        log($"[NativeUi/RectDiag]     {new string(' ', depth * 2)}{t.name} ({r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}) {tag} {GraphicInfo(t)}");
        count++;

        if (clip) return;
        for (var i = 0; i < t.childCount; i++) DumpContributors(t.GetChild(i), cam, depth + 1, ref count, log);
    }

    // Fires at resolve (no edit mode / dump-timing needed): reports the curated rect spec, the resulting
    // outline rect, and — per token — exactly which path segment FindByPath breaks on. The definitive check
    // for "did RectChild resolve" vs "fell back to auto".
    private void LogCurateResolve(RectTransform rt, Camera? cam, string allowlistPath, string? spec, WindowRect outline)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var node = ToScreenRect(rt, cam);
        _log.Info($"[NativeUi/Curate] {allowlistPath} spec='{spec ?? "(none)"}' node=({node.X:0},{node.Y:0} {node.Width:0}x{node.Height:0}) outline=({outline.X:0},{outline.Y:0} {outline.Width:0}x{outline.Height:0})");
        if (string.IsNullOrEmpty(spec)) return;
        foreach (var token in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = token.StartsWith("*") ? token.Substring(1) : token;
            var cur = rt.transform;
            var failSeg = "";
            foreach (var seg in path.Split('/'))
            {
                cur = cur == null ? null : cur.Find(seg);
                if (cur == null) { failSeg = seg; break; }
            }
            _log.Info($"[NativeUi/Curate]   token '{path}' → {(cur != null ? "FOUND" : $"FAIL at '{failSeg}'")}");
        }
    }

    // Round-trip reflow diagnostic (hypothesis: SetRect applies a saved spot as a relative TRANSLATE against a
    // transient/collapsed live size during a resolution round-trip, then the game reflows to full size afterward,
    // leaving the element offset — and the idempotent guard FREEZES it because the reflow moved sizeDelta, not
    // anchoredPosition). APPLY logs the size the translate was computed against so a later GUARD-SKIP can show it
    // differs from the settled size.
    private void LogRoundTripApply(ResolvedEntry e, WindowRect target, WindowRect liveRect, Vector2 anchored)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[NativeUi/RT] APPLY {e.Path} target=({target.X:0},{target.Y:0}) live=({liveRect.X:0},{liveRect.Y:0} {liveRect.Width:0}x{liveRect.Height:0}) anchored=({anchored.x:0.0},{anchored.y:0.0}) screen={Screen.width}x{Screen.height}");
    }

    // The smoking gun: the guard skipped (target unchanged AND anchoredPosition still where we left it) yet the
    // element's visible top-left has DRIFTED from the requested spot — which can only happen if the game reflowed
    // the element's SIZE (not its anchoredPosition) after the last apply. We log only when the drift exceeds a few
    // px so steady-state no-op skips stay silent. Includes the size-at-last-apply vs the current live size so the
    // reflow is visible in the same line.
    private void LogRoundTripGuardSkip(ResolvedEntry e, Vector2 target)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (e.RectTransform == null) return;   // element is guaranteed active here (SetRect's activeInHierarchy check ran first)
        var liveRect = ComputeOutlineRect(e.RectTransform, e.Camera, e.RectChildPath);
        var dx = liveRect.X - target.x;
        var dy = liveRect.Y - target.y;
        if (Math.Abs(dx) <= 3f && Math.Abs(dy) <= 3f) return;   // only the anomaly — steady-state skips stay silent
        var sizeAtApply = e.LastAppliedLiveSize.HasValue
            ? $"{e.LastAppliedLiveSize.Value.x:0}x{e.LastAppliedLiveSize.Value.y:0}"
            : "?";
        var frozen = e.LastAppliedAnchoredPos.HasValue
            ? $"({e.LastAppliedAnchoredPos.Value.x:0.0},{e.LastAppliedAnchoredPos.Value.y:0.0})"
            : "?";
        _log.Info($"[NativeUi/RT] GUARD-SKIP-WHILE-DRIFTED {e.Path} target=({target.x:0},{target.y:0}) live=({liveRect.X:0},{liveRect.Y:0}) drift=({dx:0.0},{dy:0.0}) liveSize={liveRect.Width:0}x{liveRect.Height:0} sizeAtApply={sizeAtApply} frozenAnchored={frozen}");
    }

    private static string GraphicInfo(Transform t)
    {
        var g = t.GetComponent<Graphic>();
        if (g == null) return "graphic=none";
        var cr = g.canvasRenderer;
        return $"enabled={g.enabled} cull={(cr != null && cr.cull)} a={g.color.a:0.00}";
    }
}
