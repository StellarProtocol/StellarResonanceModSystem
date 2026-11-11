using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Stellar.Abstractions.Domain;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Screen-rect geometry for <see cref="PandaHudAdapter"/>: world-corner to screen conversion, the canvas camera,
/// and the visible-drawable content union the curated edit outline is built from. Split out of the main file to
/// keep it under the 500-LoC gate; pure geometry (reads only, never writes a game node).
/// </summary>
internal sealed partial class PandaHudAdapter
{
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
    private static WindowRect ContentUnionRect(RectTransform root, Camera? cam, WindowRect fallback,
        System.Collections.Generic.List<Transform>? exclude = null)
        => UnionWith(root, cam, fallback, new BoundsAcc { Exclude = exclude }, out _);

    // any = false when nothing visible was found (the fallback is returned).
    // SpecUnionRect: a curated "*" token of an allowlist RectChild spec — walk 2 levels deeper and clip only at REAL enabled
    // Mask/RectMask2D components (not the auto union's "named mask" heuristic, which can bound a non-clipping node and
    // cut drawn content: the quickbar [L Ctrl] rings, 11-11). The auto union (no spec) keeps the old, tighter rules.
    private static WindowRect SpecUnionRect(RectTransform root, Camera? cam, WindowRect fallback,
        System.Collections.Generic.List<Transform>? exclude, out bool any)
        => UnionWith(root, cam, fallback, SpecAcc(exclude), out any);

    private static BoundsAcc SpecAcc(System.Collections.Generic.List<Transform>? exclude)
        => new() { Exclude = exclude, MaxDepth = ContentMaxDepth + SpecExtraDepth, NameClip = false };

    private static WindowRect UnionWith(RectTransform root, Camera? cam, WindowRect fallback, BoundsAcc acc, out bool any)
    {
        AccumulateContentBounds(root.transform, cam, depth: 0, acc);
        any = acc.Any;
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
        if (t == null) return;
        if (depth > acc.MaxDepth) { acc.DepthCapped = true; return; }   // flags for the [NativeUi/Curate] diagnostics
        if (acc.Budget <= 0) { acc.BudgetHit = true; return; }
        acc.Budget--;
        if (acc.IsExcluded(t)) return;               // a "-" token of the outline spec (OutlineSpec)
        if (!t.gameObject.activeInHierarchy) return; // hidden subtree contributes nothing
        var rt = t.TryCast<RectTransform>();
        if (rt == null) return;

        // A clip region (Mask/RectMask2D, e.g. the minimap's circular mask or a
        // chat scroll viewport) bounds everything below it — its children pan
        // freely behind it (the minimap map texture is 1833px). Bound the mask
        // rect itself and stop, so clipped content can't balloon the box.
        if (IsClip(t, acc.NameClip))
        {
            AddCorners(rt, cam, acc);
            acc.ClipLog?.Add(ClipEntry(t, rt, cam));   // diagnostics only ([NativeUi/Curate] clip list; not evaluated when null)
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

    private const int SpecExtraDepth = 2;

    // A clip region: an ENABLED RectMask2D / Mask (a disabled one clips nothing), or — auto union only (nameClip) — a
    // node merely NAMED mask, the original minimap heuristic.
    private static bool IsClip(Transform t, bool nameClip = true)
    {
        var r2d = t.GetComponent<RectMask2D>();
        if (r2d != null && r2d.enabled) return true;
        var m = t.GetComponent<Mask>();
        if (m != null && m.enabled) return true;
        return nameClip && (t.name == "mask" || t.name == "Mask");
    }

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
        public bool DepthCapped, BudgetHit;   // diagnostics: the walk was cut by the depth cap / node budget
        public int MaxDepth = ContentMaxDepth;
        public bool NameClip = true;
        public System.Collections.Generic.List<string>? ClipLog;   // set only by the diagnostics walk
        public System.Collections.Generic.List<Transform>? Exclude;   // subtrees skipped by the union ("-" spec tokens)
        public bool IsExcluded(Transform t)
        {
            if (Exclude == null) return false;
            for (var i = 0; i < Exclude.Count; i++) if (Exclude[i].Pointer == t.Pointer) return true;
            return false;
        }
        public void Add(float x, float y)
        {
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
            Any = true;
        }
    }
}
