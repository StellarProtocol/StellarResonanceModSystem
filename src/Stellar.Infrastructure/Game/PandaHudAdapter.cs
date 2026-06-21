using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;
using UnityEngine.UI;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// INativeUiAdapter implementation that resolves Panda.Hud Canvas GameObjects
/// by path, caches the resolved transform + its original pose, and applies
/// position / visibility mutations via UnityEngine RectTransform.
/// </summary>
/// <remarks>
/// SetRect translates the target by the screen-space delta between its captured
/// original top-left and the requested top-left, expressed in the PARENT's
/// local space — it does NOT touch the element's own anchors / pivot / size.
/// An earlier impl re-anchored to (0,1)/(0,1) and wrote screen coordinates,
/// which only works for a direct child of a full-screen canvas; the real Panda
/// HUD nodes are deeply nested (their parent is not screen-sized), so that
/// re-anchor flung them to the corner. Screen Y is bottom-up; the Settings UI
/// Rect.Y is top-down, so we flip via Screen.height on the way in.
/// </remarks>
internal sealed partial class PandaHudAdapter : INativeUiAdapter
{
    private readonly Dictionary<string, ResolvedEntry> _cache = new();
    private readonly IPluginLog _log;

    public PandaHudAdapter(IPluginLog log) { _log = log; }

    public bool TryResolve(string allowlistPath, string? rectChildPath, out NativeUiHandle handle)
    {
        if (_cache.TryGetValue(allowlistPath, out var cached) && cached.GameObject != null)
        {
            handle = cached.ToHandle();
            return true;
        }

        // Carry the last real-size outline across a re-resolve (the game destroys + rebuilds these nodes on a
        // scene change, so we get a fresh entry). Without this, LastVisibleRect resets to null and — when the
        // rebuild happens during a loading/cutscene collapse — the edit-mode grab-box drops to the corner because
        // there's no good rect to hold and OriginalScreenRect is itself collapsed. The user's saved position is
        // the same across scenes, so the carried rect is the right thing to show until the element expands again.
        WindowRect? carriedVisible = _cache.TryGetValue(allowlistPath, out var prev) ? prev.LastVisibleRect : null;

        var go = GameObject.Find(allowlistPath);
        if (go == null) go = SearchByPath(allowlistPath);
        if (go == null) { handle = default; return false; }

        var rt = go.GetComponent<RectTransform>();
        if (rt == null)
        {
            _log.Warning($"[NativeUi] '{allowlistPath}' resolved but has no RectTransform; skipping.");
            handle = default;
            return false;
        }

        var entry = BuildEntry(allowlistPath, go, rt, rectChildPath, carriedVisible);
        _cache[allowlistPath] = entry;
        LogCurateResolve(rt, entry.Camera, allowlistPath, rectChildPath, entry.OriginalScreenRect);   // diagnostics (gated)
        handle = entry.ToHandle();
        return true;
    }

    // Capture a freshly-resolved node's original pose + outline. Split out of TryResolve to keep it under the
    // method-size cap (STELLAR0002). carriedVisible holds the prior entry's last real-size outline across a
    // re-resolve so the edit box keeps the user's spot through a loading/cutscene rebuild.
    private ResolvedEntry BuildEntry(string allowlistPath, GameObject go, RectTransform rt, string? rectChildPath, WindowRect? carriedVisible)
    {
        var cam = GetCanvasCamera(rt);
        var entry = new ResolvedEntry
        {
            Path = allowlistPath,
            GameObject = go,
            RectTransform = rt,
            Camera = cam,
            OriginalAnchorMin = rt.anchorMin,
            OriginalAnchorMax = rt.anchorMax,
            OriginalPivot = rt.pivot,
            OriginalAnchoredPos = rt.anchoredPosition,
            OriginalSizeDelta = rt.sizeDelta,
            OriginalActiveSelf = go.activeSelf,
            RectChildPath = rectChildPath,
            OriginalScreenRect = ComputeOutlineRect(rt, cam, rectChildPath),
        };
        // Prefer the carried last-good outline; else seed from OriginalScreenRect only if it isn't itself a
        // collapsed stub (resolved mid-loading).
        if (carriedVisible.HasValue) entry.LastVisibleRect = carriedVisible;
        else if (!IsCollapsedStub(entry.OriginalScreenRect)) entry.LastVisibleRect = entry.OriginalScreenRect;
        return entry;
    }

    // The edit-outline / grab-box rect. When the allowlist supplies a spec, it's a ';'-separated list of
    // Transform.Find sub-paths whose rects are UNIONED — so a panel made of separated pieces (HP bar + stamina
    // bar, excluding the class gauge between them) frames tightly. Each token is the child's OWN rect by
    // default (tight to a container, e.g. the quickbar bar = 'button_pos_group', which excludes the skill
    // drawer that floats far away); prefix a token with '*' to use the visible-drawable UNION of that subtree
    // instead (when the wanted box is a node's contents that overflow its own rect, e.g. a bar plus the number
    // label anchored above it). No spec → the auto rect (usable node rect, else visible-drawable union).
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

    // The element's CURRENT curated screen rect, recomputed live while ACTIVE — so the edit outline tracks the
    // element through animations / layout changes (e.g. the party panel growing 5→20 person). When the element
    // is HIDDEN (inactive), its world-corners / content-union are invalid (children skipped → collapses to ~0
    // or jumps to a different node rect), so return the last rect cached while it was visible — a stable,
    // non-zero "re-enable" outline that doesn't jump on hide/show.
    public WindowRect GetCurrentRect(NativeUiHandle handle)
    {
        if (!_cache.TryGetValue(handle.AllowlistPath, out var e) || e.RectTransform == null) return handle.OriginalRect;
        if (!e.RectTransform.gameObject.activeInHierarchy)
            return e.LastVisibleRect ?? e.OriginalScreenRect;
        var r = ComputeOutlineRect(e.RectTransform, e.Camera, e.RectChildPath);
        // While the game collapses its HUD (loading / cutscene), elements are active but shrink to a ~1px stub at
        // the screen edge, so the live rect is degenerate. Don't let the edit-mode grab-box follow it down to the
        // corner, and NEVER cache a stub — hold the last rect seen at real size (carried across re-resolves, see
        // TryResolve) so the box stays at the user's spot through the transition. A real widget is never tiny in
        // BOTH axes when shown (a thin bar is still wide), so this only catches the collapsed/transient case.
        if (IsCollapsedStub(r)) return e.LastVisibleRect ?? e.OriginalScreenRect;
        e.LastVisibleRect = r;   // cache only real-size rects, for the inactive/collapsed fallback
        return r;
    }

    // Pull a rect fully on-screen (top-left in [0, screen-size]) when it fits; larger-than-screen elements keep
    // their top-left at 0. Keeps a moved game-UI element from clipping off an edge in a wider layout.
    private static WindowRect ClampFullyOnScreen(WindowRect r)
    {
        var x = Math.Clamp(r.X, 0f, Math.Max(0f, Screen.width  - r.Width));
        var y = Math.Clamp(r.Y, 0f, Math.Max(0f, Screen.height - r.Height));
        return new WindowRect(x, y, r.Width, r.Height);
    }

    public void SetVisible(NativeUiHandle handle, bool visible)
    {
        if (!_cache.TryGetValue(handle.AllowlistPath, out var e) || e.GameObject == null) return;
        if (e.GameObject.activeSelf == visible) return;
        e.GameObject.SetActive(visible);
    }

    public void SetRect(NativeUiHandle handle, WindowRect rect)
    {
        if (!_cache.TryGetValue(handle.AllowlistPath, out var e) || e.RectTransform == null) return;
        var rt = e.RectTransform;
        // Never reposition a HIDDEN element. The game deactivates its HUD during cutscenes / scene transitions,
        // and an inactive RectTransform reports garbage world-corners (GetWorldCorners on a culled subtree
        // collapses to ~0 / jumps) — see GetCurrentRect. Translating by the resulting bogus delta flings the
        // element thousands of px off-screen, and it STAYS there when the game re-shows it after the cutscene
        // (the reported "in-game UI disappears after a cutscene"). Skip while hidden; ReassertAll re-applies the
        // saved pose once the element is live again.
        if (!rt.gameObject.activeInHierarchy) return;
        // Idempotent guard: skip ONLY when the request is unchanged AND the element is still where we last left
        // it. Keying on the request alone is wrong — after a cutscene / scene transition the game resets the
        // element back to its default pose while we still request the SAME saved spot, so a request-only guard
        // short-circuits and the element stays at the game default (the "bars revert to bottom-left" report).
        // Comparing the live anchoredPosition to the value we left it at re-applies the saved spot once the game
        // has moved it. (The inactive-guard above means this only ever runs on a live element, never on the
        // inactive/garbage-corner case that caused the off-screen fling.)
        var target = new Vector2(rect.X, rect.Y);
        if (e.LastAppliedTarget.HasValue && e.LastAppliedTarget.Value == target
            && e.LastAppliedAnchoredPos.HasValue && rt.anchoredPosition == e.LastAppliedAnchoredPos.Value) return;

        var parent = rt.parent != null ? rt.parent.TryCast<RectTransform>() : null;
        if (parent == null) return; // need a RectTransform parent to translate in its local space

        // Translate RELATIVE TO THE ELEMENT'S CURRENT live position (not the resolve-time snapshot): the move
        // is the parent-local delta from where the element is NOW to the requested top-left. This makes a
        // zero-movement click a true no-op (target == current → delta 0) and a drag move by exactly the pointer
        // delta — robust to the element being a different size/position than at resolve (e.g. the party panel
        // switching between the 5- and 20-person layout). Clamp the requested top-left (by the current size) so
        // a saved spot can't push a wider layout off-screen. No-op for elements that already fit.
        var liveRect = ComputeOutlineRect(rt, e.Camera, e.RectChildPath);
        var clamped = ClampFullyOnScreen(new WindowRect(rect.X, rect.Y, liveRect.Width, liveRect.Height));
        var curTopLeft = new Vector2(liveRect.X, Screen.height - liveRect.Y);
        var newTopLeft = new Vector2(clamped.X, Screen.height - clamped.Y);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, curTopLeft, e.Camera, out var localCur);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, newTopLeft, e.Camera, out var localNew);
        var delta = localNew - localCur;
        // Sanity cap: a parent-local delta larger than this means the live geometry was bogus (a transient /
        // mid-animation frame gave garbage corners). Never translate by it — that's what flung elements
        // thousands of px off-screen. Leave LastAppliedAnchoredPos unset so a sane frame re-applies cleanly.
        if (Mathf.Abs(delta.x) > MaxSaneDeltaPx || Mathf.Abs(delta.y) > MaxSaneDeltaPx) return;
        rt.anchoredPosition += delta;
        e.LastAppliedTarget = target;
        e.LastAppliedAnchoredPos = rt.anchoredPosition;
    }

    public void RestoreOriginal(NativeUiHandle handle)
    {
        if (!_cache.TryGetValue(handle.AllowlistPath, out var e)) return;
        if (e.RectTransform != null)
        {
            e.RectTransform.anchorMin        = e.OriginalAnchorMin;
            e.RectTransform.anchorMax        = e.OriginalAnchorMax;
            e.RectTransform.pivot            = e.OriginalPivot;
            e.RectTransform.anchoredPosition = e.OriginalAnchoredPos;
            e.RectTransform.sizeDelta        = e.OriginalSizeDelta;
        }
        if (e.GameObject != null && e.GameObject.activeSelf != e.OriginalActiveSelf)
            e.GameObject.SetActive(e.OriginalActiveSelf);
        // Clear last-applied cache so the next SetRect call re-writes the
        // pose instead of short-circuiting on the stale comparison.
        e.LastAppliedTarget = null;
        e.LastAppliedAnchoredPos = null;
    }

    private static GameObject? SearchByPath(string path)
    {
        // Slow fallback: walk every loaded Canvas and try the leaf subtree.
        var idx = path.IndexOf('/');
        var leafPath = idx >= 0 ? path.Substring(idx + 1) : path;
        foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            var found = canvas.transform.Find(leafPath);
            if (found != null) return found.gameObject;
        }
        return null;
    }

    private static WindowRect ToScreenRect(RectTransform rt, Camera? cam)
    {
        // Il2CppInterop: GetWorldCorners fills the array IN PLACE. A managed
        // Vector3[] gets coerced to a throwaway Il2Cpp copy that the method
        // writes into, leaving the managed array all-zeros — so allocate the
        // Il2Cpp array explicitly and read back from it.
        var corners = new Il2CppStructArray<Vector3>(4);
        rt.GetWorldCorners(corners);
        // World corners -> screen px. For a ScreenSpaceCamera canvas this needs
        // the canvas camera; passing null (Overlay) treats world == screen.
        var min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]); // bottom-left
        var max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]); // top-right
        return new WindowRect(min.x, Screen.height - max.y, max.x - min.x, max.y - min.y);
    }

    // The render camera for the element's canvas, or null for ScreenSpaceOverlay.
    // Needed by both world->screen (ToScreenRect) and screen->local (SetRect):
    // the game's HUD canvas is ScreenSpaceCamera, where null gives wrong coords.
    private static Camera? GetCanvasCamera(RectTransform rt)
    {
        var canvas = rt.GetComponentInParent<Canvas>();
        if (canvas == null) return null;
        var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
        return root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
    }

    // Most Panda HUD groups are full-screen or zero-size CONTAINER nodes whose
    // visible widget is assembled from absolutely-positioned children — so the
    // node's own rect is useless for an outline/grab-box. Instead bound the
    // union of the drawable descendants (those carrying a CanvasRenderer). A
    // depth cap keeps masked/scrolling content (e.g. the minimap's map markers,
    // which live many levels down and pan outside the visible mask) from
    // ballooning the box back to full-screen.
    private const int ContentMaxDepth = 6;
    private const int ContentNodeBudget = 1500;

    // Largest parent-local translate SetRect will ever apply. A legit cross-screen move is bounded by the
    // parent's local size (~ canvas size); anything past this is bogus geometry from a transient frame, so we
    // refuse it rather than fling the element off-screen.
    private const float MaxSaneDeltaPx = 6000f;

    private static WindowRect ComputeContentScreenRect(RectTransform root, Camera? cam)
    {
        // Prefer the node's OWN rect when it's a real widget bound. Many HUD groups whose own rect is useful
        // (the quickbar = 700x90, the exp bar, the profession-buff group) were being over-expanded by unioning
        // spatially-separated descendants — e.g. the quickbar's skill-drawer / extra slot floats far above the
        // main bar, ballooning a 700x90 bar into 959x358. The union is only needed for the genuine CONTAINER
        // nodes (party/minimap/chat/quest = full-screen, player-HP = zero-width), whose own rect is useless.
        var nodeRect = ToScreenRect(root, cam);
        if (IsUsableNodeRect(nodeRect)) return nodeRect;
        return ContentUnionRect(root, cam, nodeRect);
    }

    // The screen-rect bounding the visible (non-transparent/enabled/un-culled) drawable descendants of
    // <paramref name="root"/>, or <paramref name="fallback"/> when nothing draws.
    private static WindowRect ContentUnionRect(RectTransform root, Camera? cam, WindowRect fallback)
    {
        var acc = new BoundsAcc();
        AccumulateContentBounds(root.transform, cam, depth: 0, acc);
        if (!acc.Any) return fallback;
        return new WindowRect(acc.MinX, Screen.height - acc.MaxY, acc.MaxX - acc.MinX, acc.MaxY - acc.MinY);
    }

    // A node rect is usable as the outline directly when it's neither a full-screen container (party/minimap/
    // chat/quest all report the canvas's 1920x1080) nor a zero-size anchor (player-HP's hide-root is 0-wide).
    private static bool IsUsableNodeRect(WindowRect r)
    {
        if (r.Width < 1f || r.Height < 1f) return false;
        return !(r.Width >= Screen.width * 0.9f && r.Height >= Screen.height * 0.9f);
    }

    // A collapsed/auto-hidden stub: tiny in BOTH axes (the game shrinks HUD nodes to ~1px during loading /
    // cutscenes; the idle chat icon is ~18x10). Requiring both axes means a wide-but-thin bar (HP / stamina /
    // class-gauge, hundreds of px × ~10-15) — a real shown widget — is never mistaken for a stub.
    private const float StubMaxPx = 24f;
    private static bool IsCollapsedStub(WindowRect r) => r.Width < StubMaxPx && r.Height < StubMaxPx;

    private static void AccumulateContentBounds(Transform t, Camera? cam, int depth, BoundsAcc acc)
    {
        if (t == null || depth > ContentMaxDepth || acc.Budget <= 0) return;
        acc.Budget--;
        if (!t.gameObject.activeInHierarchy) return; // hidden subtree contributes nothing
        var rt = t.TryCast<RectTransform>();
        if (rt == null) return;

        // A clip region (Mask/RectMask2D, e.g. the minimap's circular mask or a
        // chat scroll viewport) bounds everything below it — its children pan
        // freely behind it (the minimap map texture is 1833px). Bound the mask
        // rect itself and stop, so clipped content can't balloon the box.
        if (IsClip(t))
        {
            AddCorners(rt, cam, acc);
            return;
        }

        // Only count a node that's ACTUALLY VISIBLE. A CanvasRenderer alone isn't enough: full-area press
        // catchers (a=0.00), unfilled placeholder slots (a=0.04), culled, or graphic-less container nodes all
        // carry a CanvasRenderer yet draw nothing — and they were inflating the union (e.g. the party panel's
        // transparent presscheck dragged the box left). The node's visible children still contribute their own
        // corners, so dropping the invisible parent never loses real bounds.
        if (IsVisibleGraphic(t)) AddCorners(rt, cam, acc);
        for (var i = 0; i < t.childCount; i++)
            AccumulateContentBounds(t.GetChild(i), cam, depth + 1, acc);
    }

    private const float MinVisibleAlpha = 0.1f;

    private static bool IsVisibleGraphic(Transform t)
    {
        var g = t.GetComponent<Graphic>();
        if (g == null || !g.enabled) return false;
        var cr = g.canvasRenderer;
        if (cr != null && cr.cull) return false;
        return g.color.a >= MinVisibleAlpha;
    }

    private static bool IsClip(Transform t)
        => t.GetComponent<RectMask2D>() != null
        || t.GetComponent<Mask>() != null
        || t.name == "mask" || t.name == "Mask";

    private static void AddCorners(RectTransform rt, Camera? cam, BoundsAcc acc)
    {
        var corners = new Il2CppStructArray<Vector3>(4);
        rt.GetWorldCorners(corners);
        for (var i = 0; i < 4; i++)
        {
            var s = RectTransformUtility.WorldToScreenPoint(cam, corners[i]);
            acc.Add(s.x, s.y);
        }
    }

    private sealed class BoundsAcc
    {
        public float MinX = float.MaxValue, MinY = float.MaxValue;
        public float MaxX = float.MinValue, MaxY = float.MinValue;
        public int  Budget = ContentNodeBudget;
        public bool Any;
        public void Add(float x, float y)
        {
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
            Any = true;
        }
    }

    private sealed class ResolvedEntry
    {
        public string Path = "";
        public GameObject? GameObject;
        public RectTransform? RectTransform;
        public Camera? Camera;
        public Vector2 OriginalAnchorMin;
        public Vector2 OriginalAnchorMax;
        public Vector2 OriginalPivot;
        public Vector2 OriginalAnchoredPos;
        public Vector2 OriginalSizeDelta;
        public bool    OriginalActiveSelf;
        public WindowRect OriginalScreenRect;
        public string? RectChildPath;   // curated rect spec — lets SetRect recompute the live curated size for on-screen clamping
        public WindowRect? LastVisibleRect;   // last curated rect while active — the editor's outline uses it when hidden (inactive)
        // Cached last-requested top-left; used by SetRect to skip the
        // translation math + write when the target hasn't drifted (Mn8).
        public Vector2? LastAppliedTarget;
        // The anchoredPosition we LEFT the element at after the last SetRect. Lets the guard tell "still where we
        // put it" (skip) from "the game reset it" (re-apply) — so a cutscene/scene reset is corrected instead of
        // leaving the element at the game default.
        public Vector2? LastAppliedAnchoredPos;

        public NativeUiHandle ToHandle() => new()
        {
            AllowlistPath = Path,
            // Opaque marker — only AllowlistPath is consulted to look up cache.
            GameObjectRef = GameObject != null ? (IntPtr)GameObject.GetInstanceID() : IntPtr.Zero,
            OriginalRect  = OriginalScreenRect,
        };
    }
}
