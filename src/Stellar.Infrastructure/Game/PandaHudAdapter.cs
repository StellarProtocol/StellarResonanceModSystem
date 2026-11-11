using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// INativeUiAdapter implementation over the Panda.Hud uGUI hierarchy. Resolves an allowlist path, measures the
/// curated screen rect for the editor, and owns the framework WRAPPER RectTransform that carries the user's offset
/// + hide (<c>PandaHudAdapter.Wrap.cs</c>).
/// </summary>
/// <remarks>
/// Why a wrapper (Native-UI-Layout-Positioning.md): translating the game's own node and <c>SetActive</c>-hiding it
/// lost races to Lua resets, open tweens and view re-parenting, and needed a 1 Hz re-assert loop with a fragile
/// (target, anchoredPosition, size) guard. Our own node is written by nobody else, so we write it once and only
/// re-check that it still exists. Coordinates: rects are screen px top-left / y-DOWN (Unity screen y is bottom-up,
/// flipped via <c>Screen.height</c> in <c>ToScreenRect</c>); wrapper offsets are the wrapper parent's local units, y-UP.
/// </remarks>
internal sealed partial class PandaHudAdapter : INativeUiAdapter
{
    private readonly Dictionary<string, ResolvedEntry> _cache = new();
    private readonly Dictionary<string, string[]> _pathSegments = new();
    private readonly IPluginLog _log;
    private Transform? _root;   // the first path segment ("zuiroot"), cached — the only GameObject.Find we do

    public PandaHudAdapter(IPluginLog log) { _log = log; }

    public bool TryResolve(NativeUiEntryDescriptor d, out NativeUiHandle handle)
    {
        if (_cache.TryGetValue(d.Path, out var cached) && cached.RectTransform != null)
        {
            handle = cached.ToHandle();
            return true;
        }
        var rt = WalkPath(d.Path)?.TryCast<RectTransform>();
        if (rt == null) { handle = default; return false; }
        if (cached != null) DiscardWrapper(cached);   // an orphaned outer wrapper of the dead element
        var entry = new ResolvedEntry
        {
            Id = d.Id, Path = d.Path, GameObject = rt.gameObject, RectTransform = rt, Camera = GetCanvasCamera(rt),
            RectChildPath = d.RectChild, Mode = d.Wrap, BlockInputWhenHidden = d.BlockInputWhenHidden,
        };
        _cache[d.Path] = entry;
        LogCurateResolve(rt, entry.Camera, d.Path, d.RectChild, ComputeOutlineRect(rt, entry.Camera, d.RectChild));   // diagnostics (gated)
        handle = entry.ToHandle();
        return true;
    }

    // Walk the FULL allowlist path one segment at a time from the cached root. Transform.Find also returns INACTIVE
    // children (GameObject.Find only sees active objects), so a solo player's hidden party panel still resolves —
    // which is what the removed every-5-s Resources.FindObjectsOfTypeAll<Canvas>() fallback used to be needed for.
    // Exact path only, never a name search: photo mode builds a SECOND FighterBtnsView copy elsewhere. Il2Cpp's
    // Transform.Find can't take a multi-segment path, hence the per-segment walk (segments split once per path).
    private Transform? WalkPath(string path)
    {
        if (!_pathSegments.TryGetValue(path, out var segs)) _pathSegments[path] = segs = path.Split('/');
        if (_root == null)
        {
            var go = GameObject.Find(segs[0]);
            if (go == null) return null;
            _root = go.transform;
        }
        var cur = _root;
        for (var i = 1; i < segs.Length && cur != null; i++) cur = cur.Find(segs[i]);
        return cur;
    }

    // The edit-outline / grab-box rect. When the allowlist supplies a spec, it's a ';'-separated list of
    // Transform.Find sub-paths whose rects are UNIONED — so a panel made of separated pieces (HP bar + stamina
    // bar, excluding the class gauge between them) frames tightly. Each token is the child's OWN rect by
    // default (tight to a container, e.g. the quickbar bar = 'button_pos_group', which excludes the skill
    // drawer that floats far away); prefix a token with '*' to use the visible-drawable UNION of that subtree
    // instead (when the wanted box is a node's contents that overflow its own rect, e.g. a bar plus the number
    // label anchored above it). No spec → the auto rect (usable node rect, else visible-drawable union).
    // rt is the element, or — for an INNER-wrapped element — our wrapper, which now holds its children (same rect).
    private static WindowRect ComputeOutlineRect(RectTransform rt, Camera? cam, string? spec)
    {
        if (string.IsNullOrEmpty(spec)) return ComputeContentScreenRect(rt, cam);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var raw in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Per-axis prefixes: "h:" contributes only the node's vertical extent, "w:" only horizontal — so a
            // rect can take its WIDTH from one node and HEIGHT from another (quest tracker: width from the title
            // bar, height down to the scroll list). "*" = visible-content union of the subtree.
            var token = raw;
            bool axisX = true, axisY = true;
            if (token.StartsWith("h:")) { axisX = false; token = token.Substring(2); }
            else if (token.StartsWith("w:")) { axisY = false; token = token.Substring(2); }
            var contentMode = token.StartsWith("*");
            var path = contentMode ? token.Substring(1) : token;
            var childRt = FindByPath(rt.transform, path)?.TryCast<RectTransform>();
            if (childRt == null) continue;
            var own = ToScreenRect(childRt, cam);
            var r = contentMode ? ContentUnionRect(childRt, cam, own) : own;
            if (axisX) { minX = Math.Min(minX, r.X); maxX = Math.Max(maxX, r.Right); }
            if (axisY) { minY = Math.Min(minY, r.Y); maxY = Math.Max(maxY, r.Bottom); }
        }
        if (minX > maxX || minY > maxY) return ComputeContentScreenRect(rt, cam);   // nothing resolved
        return new WindowRect(minX, minY, maxX - minX, maxY - minY);
    }

    // Resolve a '/'-separated child path one segment at a time. Il2Cpp's Transform.Find returns null for a
    // multi-segment path (only single-name lookup works), so we walk level by level — which also lets a name
    // legitimately contain no '/' surprises (e.g. "main_team_sub_pc(Clone)").
    private static Transform? FindByPath(Transform root, string path)
    {
        var cur = root;
        foreach (var seg in path.Split('/'))
        {
            if (cur == null) return null;
            if (seg.Length == 0 || seg == ".") continue;   // "." (or "*.") = the resolved node itself
            cur = cur.Find(seg);
        }
        return cur;
    }

    public bool IsAlive(NativeUiHandle handle)
        => _cache.TryGetValue(handle.AllowlistPath, out var e) && e.RectTransform != null;

    public NativeUiMeasure Measure(NativeUiHandle handle)
    {
        if (!_cache.TryGetValue(handle.AllowlistPath, out var e) || e.RectTransform == null) return default;
        var rt = e.RectTransform;
        // An inactive RectTransform reports garbage world corners (collapsed / jumped) — never measure it.
        if (!rt.gameObject.activeInHierarchy) return new NativeUiMeasure(default, false, PxPerUnit(e));
        // A user hide is scale 0 on OUR wrapper, which collapses the world corners. Lift it for the measurement and
        // put it back in the same frame (nothing renders in between), so a hidden element still reports the rect
        // it will reappear at — the dimmed re-enable outline — and the self-measure never sees its own hide (the
        // 2026-11-09 live check: a reassert that measured under scale 0 nudged the profile +24 px).
        var w = e.Wrapper;
        var lifted = w != null && w.localScale.x == 0f;
        WindowRect rect;
        if (lifted) w!.localScale = Vector3.one;
        try { rect = ComputeOutlineRect(OutlineRoot(e), e.Camera, e.RectChildPath); }
        finally { if (lifted) w!.localScale = Vector3.zero; }   // never leave a user-hidden element shown
        var showing = !IsCollapsedStub(rect) && GroupAlpha(rt) >= MinShowingAlpha && InnerWrapHoldsContent(e);
        return new NativeUiMeasure(rect, showing, PxPerUnit(e));
    }

    // An inner wrapper that is EMPTY or has un-adopted siblings measures its own full-stretch node rect (the "*."
    // content union falls back to it), not the element's content — a bogus default for migration / last-seen size
    // (the class-gauge bug, 2026-11-11). Report "not showing" until the 10 Hz check has adopted the content.
    private static bool InnerWrapHoldsContent(ResolvedEntry e)
        => e.Mode != NativeUiWrapMode.Inner || e.Wrapper == null
        || (e.Wrapper.childCount > 0 && e.RectTransform!.childCount == 1);

    // Effective CanvasGroup alpha from the element up the hierarchy: the game hides the whole main HUD by the root
    // CanvasGroup alpha (map open) and keeps the buff bar's group at alpha 0 at rest, both while staying active.
    private const float MinShowingAlpha = 0.05f;
    private const int GroupAlphaMaxDepth = 16;

    private static float GroupAlpha(RectTransform rt)
    {
        var a = 1f;
        var t = rt.transform;
        for (var i = 0; i < GroupAlphaMaxDepth && t != null; i++, t = t.parent)
        {
            var cg = t.GetComponent<CanvasGroup>();
            if (cg == null) continue;
            a *= cg.alpha;
            if (cg.ignoreParentGroups || a < MinShowingAlpha) break;
        }
        return a;
    }

    // Screen px per local unit of the wrapper's PARENT space (the space wrapper.anchoredPosition lives in): the
    // element's parent for an outer wrapper, the element itself for an inner one. 0 = unknown (detached view root).
    private static float PxPerUnit(ResolvedEntry e)
    {
        var rt = e.RectTransform;
        if (rt == null) return 0f;
        var space = e.Mode == NativeUiWrapMode.Inner ? rt.transform : e.Wrapper != null ? e.Wrapper.parent : rt.parent;
        if (space == null) return 0f;
        var a = RectTransformUtility.WorldToScreenPoint(e.Camera, space.TransformPoint(Vector3.zero));
        var b = RectTransformUtility.WorldToScreenPoint(e.Camera, space.TransformPoint(new Vector3(100f, 0f, 0f)));
        var px = (b - a).magnitude / 100f;
        return float.IsFinite(px) ? px : 0f;
    }

    // The node the curated rect is measured from: our inner wrapper holds an inner-wrapped element's children (it is
    // full-stretch, so its rect equals the element's), keeping RectChild sub-paths and content depths unchanged.
    private static RectTransform OutlineRoot(ResolvedEntry e)
        => e.Mode == NativeUiWrapMode.Inner && e.Wrapper != null ? e.Wrapper : e.RectTransform!;

    private sealed class ResolvedEntry
    {
        public string Id = "";
        public string Path = "";
        public GameObject? GameObject;
        public RectTransform? RectTransform;
        public Camera? Camera;
        public string? RectChildPath;   // curated rect spec (edit outline / grab box)
        public NativeUiWrapMode Mode;
        public bool BlockInputWhenHidden;
        public RectTransform? Wrapper;      // OUR node carrying offset + hide; null until the element is customized
        public CanvasGroup? WrapperGroup;   // only for BlockInputWhenHidden (chat): input off while hidden

        public NativeUiHandle ToHandle() => new()
        {
            AllowlistPath = Path,
            // Opaque marker — only AllowlistPath is consulted to look up cache.
            GameObjectRef = GameObject != null ? (IntPtr)GameObject.GetInstanceID() : IntPtr.Zero,
        };
    }
}
