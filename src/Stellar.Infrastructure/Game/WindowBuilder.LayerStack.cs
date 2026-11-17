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

    private void BuildLayerStack(LayerStackElement ls, Transform parent, WindowToken token)
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
            BuildElement(layer.Content, gos[i].transform, token);
            if (layer.Visible == null && layer.Background == null) continue;   // static: nothing to poll
            token.Layers.Add(new LayerBinding
            {
                Go = gos[i], Visible = layer.Visible, Background = layer.Background,
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
            return flipped;
        }
    }
}
