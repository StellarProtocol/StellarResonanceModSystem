using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="XYPadElement"/>: a square pad (faint grid, a slightly stronger centre crosshair, a knob-styled dot).
/// <para>Hierarchy: <c>XYPad</c> (layout cell; CanvasGroup for the disabled dim) → <c>Pad</c> (centred square background)
/// → <c>Travel</c> (the pad inset by the dot's radius — the hit area and the value space, like the slider's handle area,
/// so the dot never hangs over the edge and stays under the pointer) → <c>Grid*</c> / <c>Cross*</c> lines + <c>Dot</c>.
/// The pad is kept square: sized to the cell's shorter side, or with <c>Size == 0</c> to the cell's width while the
/// cell's height follows it (<see cref="XYPadBinding.Apply"/>), so the pad fills the cell width.</para>
/// <para>Pointer: the same drag-area path as the ColorPicker's SV square — the window interaction ticker hit-tests the
/// pad (z-order aware) on press and reports the pointer normalized with a bottom-left origin every frame of the drag;
/// <see cref="XYPadMapping"/> turns that into the value (clamped, y up) and <c>Set</c> runs on press and every drag frame,
/// like <c>SliderElement</c>'s <c>onValueChanged</c>. The sandbox registers no ticker: static render only.</para>
/// <para>Colours: background and grid are white at low alpha like the slider track, the crosshair a little stronger,
/// the dot the slider knob's colour and size (13 px capsule), with an accent ring that follows the theme.</para>
/// </summary>
internal sealed partial class WindowBuilder
{
    private const float XYPadDefaultSide = 160f;
    private const float XYPadDotSize = 13f;
    private const float XYPadDotRing = 2f;
    private static readonly Color XYPadBackground = new(1f, 1f, 1f, 0.06f);
    private static readonly Color XYPadGrid = new(1f, 1f, 1f, 0.07f);
    private static readonly Color XYPadCross = new(1f, 1f, 1f, 0.22f);
    private static readonly Color XYPadKnob = new(0.81f, 0.88f, 0.95f, 1f);   // = the slider handle
    private const float XYPadDisabledAlpha = 0.45f;

    private void BuildXYPad(XYPadElement e, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("XYPad", parent);
        var le = go.AddComponent<LayoutElement>();
        var side = e.Size > 0f ? e.Size : XYPadDefaultSide;
        le.preferredHeight = le.minHeight = side;
        if (e.Size > 0f) { le.preferredWidth = le.minWidth = side; le.flexibleWidth = 0f; }
        else { le.preferredWidth = side; le.flexibleWidth = 1f; }
        var group = go.AddComponent<CanvasGroup>();

        var pad = UGuiPrimitives.NewChild("Pad", go.transform);
        var prt = pad.GetComponent<RectTransform>();
        prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f); prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(side, side); prt.anchoredPosition = Vector2.zero;
        var bg = pad.AddComponent<Image>();
        bg.sprite = _assets.SwatchBg; bg.type = Image.Type.Sliced; bg.color = XYPadBackground; bg.raycastTarget = true;
        UGuiPrimitives.AddDragSink(pad);   // the raycast target: a drag here must not scroll the window

        var travel = UGuiPrimitives.NewChild("Travel", pad.transform);
        var trt = travel.GetComponent<RectTransform>();
        const float inset = XYPadDotSize / 2f + XYPadDotRing;
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(inset, inset); trt.offsetMax = new Vector2(-inset, -inset);
        AddXYPadGrid(travel.transform, e.GridLines);
        var dot = AddXYPadDot(travel.transform, token);

        var binding = new XYPadBinding
        {
            Cell = go.GetComponent<RectTransform>(), Pad = prt, Dot = dot, Layout = le, Group = group,
            Get = e.Get, Set = e.Set, Min = e.Min, Max = e.Max, EnabledFn = e.Enabled, FillWidth = e.Size <= 0f,
        };
        _registerDrag?.Invoke(trt, binding.OnPointer);   // pointer space = the dot's travel area
        token.XYPads.Add(binding);
    }

    // Grid: divisions-1 faint lines per axis, skipping the centre (the crosshair draws it); then the crosshair.
    private static void AddXYPadGrid(Transform pad, int divisions)
    {
        for (var i = 1; i < divisions; i++)
        {
            var f = (float)i / divisions;
            if (Mathf.Approximately(f, 0.5f)) continue;
            AddXYPadLine(pad, "GridV", f, vertical: true, XYPadGrid);
            AddXYPadLine(pad, "GridH", f, vertical: false, XYPadGrid);
        }
        AddXYPadLine(pad, "CrossV", 0.5f, vertical: true, XYPadCross);
        AddXYPadLine(pad, "CrossH", 0.5f, vertical: false, XYPadCross);
    }

    private static void AddXYPadLine(Transform pad, string name, float at, bool vertical, Color color)
    {
        var line = UGuiPrimitives.NewChild(name, pad);
        var rt = line.GetComponent<RectTransform>();
        rt.anchorMin = vertical ? new Vector2(at, 0f) : new Vector2(0f, at);
        rt.anchorMax = vertical ? new Vector2(at, 1f) : new Vector2(1f, at);
        rt.sizeDelta = vertical ? new Vector2(1f, 0f) : new Vector2(0f, 1f);
        rt.anchoredPosition = Vector2.zero;
        var img = line.AddComponent<Image>(); img.color = color; img.raycastTarget = false;
    }

    // The dot: an accent ring (follows the theme) under a knob-coloured capsule, centred on its anchor point.
    private RectTransform AddXYPadDot(Transform pad, WindowToken token)
    {
        var dot = UGuiPrimitives.NewChild("Dot", pad);
        var rt = dot.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(XYPadDotSize + XYPadDotRing * 2f, XYPadDotSize + XYPadDotRing * 2f);
        rt.anchoredPosition = Vector2.zero;
        var ring = dot.AddComponent<Image>();
        ring.sprite = _assets.Capsule; ring.type = Image.Type.Sliced; ring.color = _assets.MenuAccent; ring.raycastTarget = false;
        token.ReskinActions.Add(() => { if (ring != null) ring.color = _assets.MenuAccent; });

        var knob = UGuiPrimitives.NewChild("Knob", dot.transform);
        var krt = knob.GetComponent<RectTransform>();
        krt.anchorMin = krt.anchorMax = new Vector2(0.5f, 0.5f); krt.pivot = new Vector2(0.5f, 0.5f);
        krt.sizeDelta = new Vector2(XYPadDotSize, XYPadDotSize); krt.anchoredPosition = Vector2.zero;
        var img = knob.AddComponent<Image>();
        img.sprite = _assets.Capsule; img.type = Image.Type.Sliced; img.color = XYPadKnob; img.raycastTarget = false;
        return rt;
    }

    /// <summary>Per-apply: the dot follows <c>Get</c> (value-diffed), the enabled dim, and the square sizing. Pointer
    /// input arrives from the interaction ticker through <see cref="OnPointer"/>.</summary>
    internal sealed class XYPadBinding
    {
        public RectTransform Cell = null!, Pad = null!, Dot = null!;
        public LayoutElement Layout = null!;
        public CanvasGroup Group = null!;
        public Func<(float X, float Y)> Get = null!;
        public Action<float, float> Set = null!;
        public float Min, Max;
        public Func<bool>? EnabledFn;
        public bool FillWidth;
        private bool _init, _enabled = true, _enabledInit;
        private (float X, float Y) _last;

        public void Apply()
        {
            if (Cell == null || !Cell.gameObject.activeInHierarchy) return;
            KeepSquare();
            var v = Get();
            if (!_init || !Mathf.Approximately(v.X, _last.X) || !Mathf.Approximately(v.Y, _last.Y)) PlaceDot(v);
            ApplyEnabled();
        }

        /// <summary>Ticker callback: pointer in the pad, normalized, bottom-left origin.</summary>
        public void OnPointer(float nx, float ny)
        {
            if (!_enabled || Pad == null) return;
            var v = XYPadMapping.FromPointer(nx, ny, Min, Max);
            PlaceDot(v);   // immediate feedback; Get confirms it on the next apply
            Set(v.X, v.Y);
        }

        private void PlaceDot((float X, float Y) v)
        {
            _last = v; _init = true;
            var (nx, ny) = XYPadMapping.ToNormalized(v.X, v.Y, Min, Max);
            Dot.anchorMin = Dot.anchorMax = new Vector2(nx, ny);
            Dot.anchoredPosition = Vector2.zero;
        }

        // Pad = the cell's shorter side; a fill-width pad takes the cell's width and makes the cell as tall (the
        // height lands on the next layout pass — the pad is already square meanwhile).
        private void KeepSquare()
        {
            var r = Cell.rect;
            if (FillWidth && r.width > 0f && Mathf.Abs(Layout.preferredHeight - r.width) > 0.5f)
                Layout.preferredHeight = Layout.minHeight = r.width;
            var side = FillWidth ? r.width : Mathf.Min(r.width, r.height);
            if (side > 0f && Mathf.Abs(Pad.sizeDelta.x - side) > 0.5f) Pad.sizeDelta = new Vector2(side, side);
        }

        private void ApplyEnabled()
        {
            var enabled = EnabledFn?.Invoke() ?? true;
            if (_enabledInit && enabled == _enabled) return;
            _enabled = enabled; _enabledInit = true;
            Group.alpha = enabled ? 1f : XYPadDisabledAlpha;
        }
    }
}
