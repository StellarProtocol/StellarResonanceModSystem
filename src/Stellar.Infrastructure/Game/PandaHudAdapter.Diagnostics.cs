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

    private static string GraphicInfo(Transform t)
    {
        var g = t.GetComponent<Graphic>();
        if (g == null) return "graphic=none";
        var cr = g.canvasRenderer;
        return $"enabled={g.enabled} cull={(cr != null && cr.cull)} a={g.color.a:0.00}";
    }
}
