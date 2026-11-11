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
            Twins = ResolveTwins(rt, d.Twins), SeenGate = NewSeenGate(d.SeenGate),
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

    // The edit-outline / grab-box rect. When the allowlist supplies a spec (syntax: OutlineSpec), it's a ';'-separated
    // list of Transform.Find sub-paths whose rects are UNIONED — so a panel made of separated pieces (HP bar + stamina
    // bar, excluding the class gauge between them) frames tightly. Each token is the child's OWN rect by default;
    // '*' uses the visible-drawable UNION of that subtree instead (contents that overflow the node's own rect, e.g. the
    // quickbar's skill icons poking above its 700x90 container); "h:"/"w:" take one axis only; '-' EXCLUDES a subtree
    // from every '*' union (the quickbar's floating skill drawer). No spec → the auto rect (usable node rect, else
    // visible-drawable union). rt is the element, or — for an INNER-wrapped element — our wrapper (same rect).
    private static WindowRect ComputeOutlineRect(RectTransform rt, Camera? cam, string? spec)
        => ComputeOutlineRect(rt, cam, spec, out _);

    // hasContent = false when the spec has "*" tokens and NONE found a visible drawable (the union fell back to a node
    // rect) — e.g. Buff Timers with no timed buff: active, but nothing to frame. Measure treats that as hidden-by-game.
    private static WindowRect ComputeOutlineRect(RectTransform rt, Camera? cam, string? spec, out bool hasContent)
    {
        hasContent = true;
        if (string.IsNullOrEmpty(spec)) return ComputeContentScreenRect(rt, cam);
        int contentTokens = 0, contentFound = 0;
        var tokens = OutlineSpec.Parse(spec);
        var exclude = ResolveExcludes(rt, tokens);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (tok.Exclude) continue;
            var childRt = FindByPath(rt.transform, tok.Path)?.TryCast<RectTransform>();
            if (childRt == null) continue;
            var own = ToScreenRect(childRt, cam);
            var r = own;
            if (tok.Content)
            {
                contentTokens++;
                r = SpecUnionRect(childRt, cam, own, exclude, out var any);
                if (any) contentFound++;
            }
            if (tok.AxisX) { minX = Math.Min(minX, r.X); maxX = Math.Max(maxX, r.Right); }
            if (tok.AxisY) { minY = Math.Min(minY, r.Y); maxY = Math.Max(maxY, r.Bottom); }
        }
        hasContent = contentTokens == 0 || contentFound > 0;
        if (minX > maxX || minY > maxY) return ComputeContentScreenRect(rt, cam);   // nothing resolved
        return new WindowRect(minX, minY, maxX - minX, maxY - minY);
    }

    // The live transforms named by '-' tokens (null when the spec has none — the common case allocates nothing).
    private static List<Transform>? ResolveExcludes(RectTransform rt, IReadOnlyList<OutlineToken> tokens)
    {
        List<Transform>? list = null;
        for (var i = 0; i < tokens.Count; i++)
            if (tokens[i].Exclude && FindByPath(rt.transform, tokens[i].Path) is { } t) (list ??= new()).Add(t);
        return list;
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
        // (A twin entry measures on while any twin is active: the inactive primary's "*" union just frames nothing.)
        if (!rt.gameObject.activeInHierarchy && !AnyTwinActive(e)) return new NativeUiMeasure(default, false, PxPerUnit(e));
        // A user hide is scale 0 on OUR wrapper, which collapses the world corners. Lift it for the measurement and
        // put it back in the same frame (nothing renders in between), so a hidden element still reports the rect
        // it will reappear at — the dimmed re-enable outline — and the self-measure never sees its own hide (the
        // 2026-11-09 live check: a reassert that measured under scale 0 nudged the profile +24 px).
        var w = e.Wrapper;
        var lifted = w != null && w.localScale.x == 0f;
        WindowRect rect;
        if (lifted) w!.localScale = Vector3.one;
        bool hasContent;
        var alpha = GroupAlpha(rt);
        try
        {
            rect = ComputeOutlineRect(OutlineRoot(e), e.Camera, e.RectChildPath, out hasContent);
            if (e.Twins != null) MergeTwinOutlines(e, ref rect, ref hasContent, ref alpha);   // inside the scale lift too
        }
        finally { if (lifted) w!.localScale = Vector3.zero; }   // never leave a user-hidden element shown
        // Not showing also when the curated rect is degenerate in EITHER axis or framed nothing: an active-but-empty
        // element (Buff Timers with no timed buff, 300x0) must become a grabbable placeholder, not a zero-height box.
        var showing = hasContent && !IsCollapsedStub(rect) && rect.Width >= MinShownPx && rect.Height >= MinShownPx
            && alpha >= MinShowingAlpha && InnerWrapHoldsContent(e);
        return new NativeUiMeasure(rect, showing, PxPerUnit(e), showing && SeenGateOpen(e));   // PandaHudAdapter.SeenGate.cs
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
    private const float MinShownPx = 3f;
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
        public RectTransform[]? Twins;      // sibling nodes the game shows instead of the element; share its outer wrapper
        public ResolvedSeenGate? SeenGate;  // last-seen rest-state gate (PandaHudAdapter.SeenGate.cs); null = always open
        public Transform? TwinSpace;        // the twins' original parent (= outer wrapper's parent), cached at wrap time
        public RectTransform? Wrapper;      // OUR node carrying offset + hide; null until the element is customized
        public CanvasGroup? WrapperGroup;   // only for BlockInputWhenHidden (chat): input off while hidden
        public RectTransform? WrapParent;   // the wrapper's parent at wrap time; its pivot is mirrored onto the wrapper

        public NativeUiHandle ToHandle() => new()
        {
            AllowlistPath = Path,
            // Opaque marker — only AllowlistPath is consulted to look up cache.
            GameObjectRef = GameObject != null ? (IntPtr)GameObject.GetInstanceID() : IntPtr.Zero,
        };
    }
}
