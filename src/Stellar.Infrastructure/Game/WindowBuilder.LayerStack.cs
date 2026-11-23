using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// LayerStackElement (framework 2.33.0): overlays drawn over a base in the same rect.
//
//   LayerStack (VLG, = a Conditional container's sizing)
//   ├─ <base content>                     laid out normally → defines the stack's size
//   └─ Overlays (ignoreLayout, stretched)  never toggled, no LayoutGroup
//      ├─ Layer1 (stretched, own VLG)      SetActive-toggled by LayerBinding
//      └─ Layer2 …                         later sibling = drawn on top
//
// Why the extra "Overlays" host: a layer's SetActive must not re-lay-out the base. An activation dirties layout via
// LayoutRebuilder.MarkLayoutForRebuild, which climbs parents only while they hold a LayoutGroup. The layer's parent (the
// host) has none, so the rebuild stops at the layer's own subtree. Toggling the layers directly under the stack's VLG
// (even with ignoreLayout) would mark the stack — and every layout parent up to the window — dirty on each flip.
//
// Input (the two halves have to agree, see WindowBuilder-Patterns.md "LayerStackElement"):
//  • uGUI / EventSystem: a BlocksInput layer carries a raycast-target Image over its whole rect (alpha 0 when it has no
//    Background — an alpha-0 Graphic still draws one quad, but a culled one is not guaranteed to raycast); it is the
//    topmost hit, so Buttons/ScrollRects/drag handlers below never see the press, enter, wheel or drag (events bubble
//    UP from the hit, and the base is a sibling branch, not an ancestor). A hidden layer is SetActive(false) — never
//    alpha 0, which would still raycast. A non-blocking layer gets a CanvasGroup{blocksRaycasts=false}: it covers
//    content built later too (a lazy Conditional inside), and nested canvases (overrideSorting=false) still honour it.
//  • Ticker (geometric hit-tests that bypass the EventSystem: hover, right-click, drag-reorder, slot drag, chart pan/zoom,
//    colour picker / XY pad, scrollbar / scroll-view latches): every layer registers as a host; the ticker treats a
//    cell as unreachable while an active blocking layer drawn above the cell's layer covers the point
//    (WindowInteractionTicker.Layers.cs), and every cell inside a non-blocking layer as unreachable.
internal sealed partial class WindowBuilder
{
    /// <summary>(layer host, its stack root, blocking layer rects drawn above it, inert) → the ticker. The stack root
    /// registers as the host of the base. Null in the sandbox (no ticker → only the uGUI half applies).</summary>
    internal Action<RectTransform, RectTransform, RectTransform[], bool>? RegisterLayerHost { get; set; }

    /// <summary>(overlay wrapper, its Visible, its OnEscape) → the ticker, for StackLayer.OnEscape (2.35.0). Null in the
    /// sandbox (no Esc there).</summary>
    internal Action<RectTransform, Func<bool>?, Action>? RegisterEscapeLayer { get; set; }

    /// <param name="coverBody">Internal opt-in (ConfirmHostElement): the overlays fit the WINDOW BODY (token.Body minus
    /// the title bar — content padding included) instead of the base's rect. Default false = cover the base only.</param>
    private void BuildLayerStack(LayerStackElement ls, Transform parent, WindowToken token, bool coverBody = false)
    {
        var go = UGuiPrimitives.NewChild("LayerStack", parent);
        UGuiPrimitives.AddLayout(go, gap: 0f, columns: UGuiPrimitives.ColumnMode); ExpandColumnWidth(go);
        // Same reason as NewCondContainer: ExpandColumnWidth bubbles flexibleWidth=1 up, so inside a Row the stack would
        // swallow all leftover width. A Column parent force-expands it anyway (max(0,1)).
        go.AddComponent<LayoutElement>().flexibleWidth = 0f;
        BuildElement(ls.Base, go.transform, token);
        var overlays = ls.Overlays ?? Array.Empty<StackLayer>();
        if (overlays.Length == 0) return;

        var host = UGuiPrimitives.NewChild("Overlays", go.transform);
        UGuiPrimitives.Stretch(host);
        host.AddComponent<LayoutElement>().ignoreLayout = true;
        // Body cover: the host stays UNDER the stack (lifetime, hierarchy-based ticker cover walk and draw order are
        // unchanged) and is only re-offset to the body's rect by the layer poll (BodyFit). No body (overlay chromes,
        // sandbox) → the old base-only cover.
        var fit = coverBody && token.Body != null
            ? new BodyFit { Host = host.GetComponent<RectTransform>(), Stack = go.GetComponent<RectTransform>(), Body = token.Body, TitleBar = token.BodyTitleBar }
            : null;

        var blocks = new bool[overlays.Length];
        var rects = new RectTransform[overlays.Length];
        var gos = new GameObject[overlays.Length];
        for (var i = 0; i < overlays.Length; i++)
        {
            blocks[i] = overlays[i]?.BlocksInput == true;
            gos[i] = NewLayer(overlays[i], i + 1, host.transform, out rects[i]);
        }
        var stackRt = go.GetComponent<RectTransform>();
        if (RegisterLayerHost != null)
        {
            RegisterLayerHost(stackRt, stackRt, Pick(rects, LayerStackRules.BlockersAbove(blocks, 0)), false);
            for (var i = 0; i < overlays.Length; i++)
                RegisterLayerHost(rects[i], stackRt, Pick(rects, LayerStackRules.BlockersAbove(blocks, i + 1)), !blocks[i]);
        }
        for (var i = 0; i < overlays.Length; i++)
        {
            var layer = overlays[i];
            if (layer == null) continue;
            // Before its content, so a nested stack in it registers later (= drawn above it): the ticker's Esc order.
            if (layer.OnEscape != null) RegisterEscapeLayer?.Invoke(rects[i], layer.Visible, layer.OnEscape);
            BuildElement(layer.Content, gos[i].transform, token);
            if (layer.Visible == null && layer.Background == null && fit == null) continue;   // static: nothing to poll
            token.Layers.Add(new LayerBinding
            {
                Go = gos[i], Visible = layer.Visible, Background = layer.Background, Fit = fit,
                Img = layer.Background != null ? gos[i].GetComponent<Image>() : null, Gate = _condGate,
            });
        }
    }

    // One overlay wrapper: stretched over the stack, its own column layout for the content, optional fill/raycast Image.
    private static GameObject NewLayer(StackLayer? layer, int index, Transform host, out RectTransform rt)
    {
        var go = UGuiPrimitives.NewChild("Layer" + index, host);
        UGuiPrimitives.Stretch(go);
        rt = go.GetComponent<RectTransform>();
        if (layer == null) { go.SetActive(false); return go; }
        if (layer.Rect is { } place) PlaceLayer(rt, place);   // 2.34.0: a sub-rect of the stack (anchors, no layout work)
        UGuiPrimitives.AddLayout(go, gap: 0f, columns: UGuiPrimitives.ColumnMode);
        if (go.GetComponent<VerticalLayoutGroup>() is { } v)
        {
            // Not under a LayoutGroup, so forcing its own children never bubbles up to the stack.
            v.childForceExpandWidth = !layer.Center; v.childForceExpandHeight = !layer.Center;
            v.childAlignment = layer.Center ? TextAnchor.MiddleCenter : TextAnchor.UpperLeft;
        }
        if (layer.BlocksInput || layer.Background != null)
        {
            var img = go.AddComponent<Image>();
            img.color = Color.clear;   // LayerBinding paints a Background on its first apply
            img.raycastTarget = layer.BlocksInput;
        }
        if (!layer.BlocksInput)
        {
            var cg = go.AddComponent<CanvasGroup>();
            cg.blocksRaycasts = false;   // click-through, content built later included
        }
        // A polled layer starts hidden: the first apply shows it if Visible() says so (never a one-frame flash, and a
        // structure-only prebuild leaves it off).
        if (layer.Visible != null) go.SetActive(false);
        return go;
    }

    // StackLayer.Rect → anchors + offsets per axis (see LayerRect): both insets = stretch between them; near inset only =
    // pinned to that edge at a fixed size (0 = to the far edge); far inset only = pinned to the far edge. Pure anchors, so
    // the rect follows a stack resize with no per-refresh work and the ticker's blocker rect is the placed rect.
    private static void PlaceLayer(RectTransform rt, LayerRect p)
    {
        var x = Axis(p.Left, p.Right, p.Width);
        var y = Axis(p.Bottom, p.Top, p.Height);
        rt.anchorMin = new Vector2(x.x, y.x); rt.anchorMax = new Vector2(x.y, y.y);
        rt.offsetMin = new Vector2(x.z, y.z); rt.offsetMax = new Vector2(x.w, y.w);

        // lo = inset from the min edge (left / bottom), hi = inset from the max edge (right / top).
        // Returns (anchorMin, anchorMax, offsetMin, offsetMax) for that axis.
        static Vector4 Axis(float? lo, float? hi, float size)
        {
            if (lo is { } l && hi is { } h) return new Vector4(0f, 1f, l, -h);
            if (lo is { } l2) return size > 0f ? new Vector4(0f, 0f, l2, l2 + size) : new Vector4(0f, 1f, l2, 0f);
            if (hi is { } h2) return size > 0f ? new Vector4(1f, 1f, -h2 - size, -h2) : new Vector4(0f, 1f, 0f, -h2);
            return new Vector4(0f, 1f, 0f, 0f);
        }
    }

    private static RectTransform[] Pick(RectTransform[] rects, int[] idx)
    {
        if (idx.Length == 0) return Array.Empty<RectTransform>();
        var r = new RectTransform[idx.Length];
        for (var i = 0; i < idx.Length; i++) r[i] = rects[idx[i]];
        return r;
    }

    /// <summary>Per-apply poll of one overlay: Visible → SetActive (only on change), Background → colour (only on
    /// change, only while shown). Never touches layout outside the layer (see the file header).</summary>
    internal sealed class LayerBinding : GatedBinding
    {
        public GameObject Go = null!;
        public Func<bool>? Visible;
        public Func<ColorRgba>? Background;
        public Image? Img;
        public GameObject? Gate;   // enclosing variable-list slot: idle while the slot is inactive (as CondBinding)
        public BodyFit? Fit;       // body-cover stacks only: re-offset the overlays host to the window body while shown
        private bool _init, _shown, _colorInit;
        private ColorRgba _color;

        /// <summary>Returns true when the layer was shown or hidden by this apply (→ hover rescan, not a relayout).</summary>
        public bool Apply()
        {
            if (Gate != null && !Gate.activeSelf) return false;
            var flipped = false;
            var shown = Visible == null || Visible();
            if (!_init || shown != _shown)
            {
                if (Go != null) Go.SetActive(shown);
                flipped = _init;   // the first resolve isn't a flip (the mount already marked the hover pass dirty)
                _init = true; _shown = shown;
            }
            if (shown && Background != null && Img != null)
            {
                var c = Background();
                if (!_colorInit || !c.Equals(_color))
                {
                    _color = c; _colorInit = true;
                    Img.color = new Color(c.R, c.G, c.B, c.A);
                }
            }
            if (shown) Fit?.Apply();   // same apply as the show → the first visible frame already spans the body
            return flipped;
        }
    }

    /// <summary>Keeps a body-cover stack's overlays host on the window body: the root's rect minus the title bar (its
    /// LIVE height, read off the chrome), mapped into the stack's space and written as stretch offsets. So the overlay
    /// spans the content padding edge to edge, follows a resize, and never reaches the title bar (✕ + drag stay live).
    /// Polled only while a layer of the stack is shown (10 Hz apply); writes only on change; no allocation.</summary>
    internal sealed class BodyFit
    {
        public RectTransform Host = null!, Stack = null!, Body = null!;
        public RectTransform? TitleBar;

        public void Apply()
        {
            if (Host == null || Stack == null || Body == null) return;
            var r = Body.rect;
            var top = r.yMax - (TitleBar != null && TitleBar.gameObject.activeSelf ? TitleBar.rect.height : 0f);
            Vector2 bl = Stack.InverseTransformPoint(Body.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
            Vector2 tr = Stack.InverseTransformPoint(Body.TransformPoint(new Vector3(r.xMax, top, 0f)));
            var sr = Stack.rect;
            var min = bl - sr.min;
            var max = tr - sr.max;
            if ((Host.offsetMin - min).sqrMagnitude > 0.0001f) Host.offsetMin = min;
            if ((Host.offsetMax - max).sqrMagnitude > 0.0001f) Host.offsetMax = max;
        }
    }

    public sealed partial class WindowToken
    {
        // The window body a body-cover LayerStack fits (set by BuildChrome; null for chromes without one → base-only).
        internal RectTransform? Body;
        internal RectTransform? BodyTitleBar;   // the title bar sitting on top of Body (excluded from the cover)
        internal int BodyRadius;                // corner radius of Body's frame (0 = square)
    }
}
