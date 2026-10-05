using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// TextArea (multi-line) mode of <see cref="UGuiTextInput"/>: legacy uGUI InputField has NO scrollbar support — a
/// fixed-height multi-line field only scrolls through its own internal "draw window" (it re-slices the visible
/// substring to keep the caret in view), with no bar, no wheel and no sign that text is hidden. So the field is
/// wrapped in a ScrollRect instead:
/// <code>
/// Box  (themed bg · LayoutElement pinned, priority 2 · ScrollRect)
///  └ Viewport  (inset TextAreaPadY top/bottom · RectMask2D)
///     └ Field  (= ScrollRect.content · top-anchored, stretch-x · transparent click/wheel catcher Image ·
///              LayoutElement minHeight = viewport · ContentSizeFitter vertical PreferredSize · InputField)
///        └ Text (stretch, inset TextAreaPadX left / PadX + bar lane right, slack row below)
///  └ Scrollbar (WindowBuilder.BuildScrollbar, inset by <see cref="InsetTextAreaScrollbar"/>)
/// </code>
/// The Field GROWS to its wrapped-text height (InputField's own ILayoutElement preferredHeight), so the whole text
/// always "fits" from InputField's point of view and its draw window never slices — the ScrollRect does the scrolling,
/// and <see cref="TickTextArea"/> nudges it to follow the caret.
/// </summary>
internal sealed partial class UGuiTextInput
{
    /// <summary>Inner padding: box edge → text, left/right (the left inset rides ApplyStyle's leftPad).</summary>
    internal const float TextAreaPadX = 8f;
    /// <summary>Inner padding: box edge → text, top/bottom — INSIDE the fixed box height.</summary>
    internal const float TextAreaPadY = 6f;
    private const float TextAreaBarWidth = 5f;     // = BuildScrollbar's width
    private const float TextAreaBarEdgeGap = 3f;   // bar sits inside the right padding, 3 px off the box edge
    private const int FollowFrames = 3;            // frames a caret/text change keeps re-checking the caret line

    private ScrollRect? _scroll;
    private RectTransform? _fieldRect;
    private float _textRightReserve;               // extra right text inset (TextArea: the bar lane; else 0)
    private Vector2 _lastFieldSize = new(-1f, -1f);
    private int _lastCaret = -1;
    private int _followUntilFrame = -1;
    private CanvasRenderer? _caretRenderer;        // InputField's lazily-created caret/selection renderer (ClipCaret)
    private readonly List<RectMask2D> _clipMasks = new();   // every RectMask2D from the Viewport up (ClipCaret)

    /// <summary>TextArea mode: the ScrollRect the owner hangs its themed scrollbar on (null in other modes).</summary>
    internal ScrollRect? TextAreaScroll => _scroll;

    // Box height = lines × 16-px rows + top/bottom padding; the box is pinned at priority 2 (out-ranks InputField's
    // own ILayoutElement on the tie — see ConfigureFixedBox) so it never grows. The pin lives on the OUTER box only:
    // the Field must NOT be pinned, because its InputField-reported preferred height is exactly what we want to grow
    // the scroll content. BuildTextArea (WindowBuilder) sets preferredWidth/flexibleWidth on this same LayoutElement.
    private GameObject BuildTextArea(Transform parent, int lines)
    {
        float viewH = (lines < 1 ? 1 : lines) * MultiLineRowPx;
        float boxH = viewH + 2f * TextAreaPadY;
        var box = NewChild("UGuiTextArea", parent);
        var le = box.AddComponent<LayoutElement>();
        le.minHeight = boxH; le.preferredHeight = boxH; le.flexibleHeight = 0f;
        le.layoutPriority = 2;
        var bg = box.AddComponent<Image>();
        bg.color = new Color(0.95f, 0.95f, 0.95f, 1f);
        _bg = bg;   // ApplyStyle themes the OUTER box (the field's own Image is a transparent catcher)
        var sr = box.AddComponent<ScrollRect>();
        sr.horizontal = false; sr.vertical = true; sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 24f;   // same feel as WindowBuilder.BuildScroll
        _scroll = sr;

        // Viewport spans the FULL width (so the Field — and its click catcher — covers the left/right padding and the
        // bar lane: a click there still focuses the field) but is inset top/bottom, so scrolled text clips cleanly
        // INSIDE the padding rather than against the rounded border.
        var viewport = NewChild("Viewport", box.transform);
        Stretch(viewport);
        var vrt = viewport.GetComponent<RectTransform>();
        vrt.offsetMin = new Vector2(0f, TextAreaPadY); vrt.offsetMax = new Vector2(0f, -TextAreaPadY);
        viewport.AddComponent<RectMask2D>();
        _textRightReserve = TextAreaBarWidth;   // text clears the bar: right inset = PadX + bar width
        BuildGrowingField(viewport.transform, viewH);
        sr.viewport = vrt; sr.content = _fieldRect;
        return box;
    }

    // The scroll content IS the field. Top-anchored + stretch-x with sizeDelta ZEROED (a fresh RectTransform's
    // leftover sizeDelta.x would make it wider than the viewport — the BuildScroll "clip" bug). minHeight = viewport
    // height so a click anywhere in the empty box lands on the field; preferred = InputField's wrapped-text height.
    // The transparent Image is the raycast target for clicks AND the wheel: InputField has no IScrollHandler, so
    // OnScroll bubbles up the hierarchy to the box's ScrollRect.
    private void BuildGrowingField(Transform viewport, float viewH)
    {
        var go = NewChild("Field", viewport);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = Vector2.zero; rt.anchoredPosition = Vector2.zero;
        _fieldRect = rt;
        var catcher = go.AddComponent<Image>();
        catcher.color = new Color(0f, 0f, 0f, 0f); catcher.raycastTarget = true;
        var le = go.AddComponent<LayoutElement>(); le.minHeight = viewH;
        var fit = go.AddComponent<ContentSizeFitter>();
        fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;   // width stays anchor-locked to the viewport
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var txt = CreateText(go.transform);
        // One row of SLACK below the field: InputField re-slices its draw window whenever the text's lines don't fit
        // the TEXT rect (float rounding at "exactly fits", or a just-typed last line before layout catches up) and
        // would then hide the first line. UpperLeft text never draws into the slack; the caret quad copies this rect.
        txt.rectTransform.offsetMin = new Vector2(0f, -MultiLineRowPx);
        AttachField(go, txt, catcher);
    }

    /// <summary>TextArea: inset the themed bar (built by WindowBuilder.BuildScrollbar on the box) so it sits inside
    /// the padding — TextAreaBarEdgeGap off the right edge, TextAreaPadY off top/bottom. No-op in other modes.</summary>
    internal void InsetTextAreaScrollbar()
    {
        var bar = _scroll != null ? _scroll.verticalScrollbar : null;
        if (bar == null) return;
        var rt = bar.GetComponent<RectTransform>();   // anchors (1,0)-(1,1), pivot (1,1) from BuildScrollbar
        rt.anchoredPosition = new Vector2(-TextAreaBarEdgeGap, -TextAreaPadY);
        rt.sizeDelta = new Vector2(TextAreaBarWidth, -2f * TextAreaPadY);
    }

    /// <summary>Select/copy-only (TextAreaElement.ReadOnly). Stock uGUI <c>readOnly</c> gates only the EDIT paths
    /// (Append, Backspace/Delete, paste, cut, IME); focus, caret, mouse/keyboard selection, Ctrl+A and Ctrl+C still
    /// run, so IsFocused / the keyboard gate / caret-follow are untouched. The <c>text</c> setter is NOT gated, so the
    /// FieldBinding re-seed from Get() keeps working. No-op if not built.</summary>
    internal void SetReadOnly(bool readOnly)
    {
        if (_field != null) _field.readOnly = readOnly;
    }

    // onValueChanged fires BEFORE InputField's UpdateLabel (SendOnValueChangedAndUpdateLabel). Growing the field NOW
    // means UpdateLabel slices against the new height (whole text fits → no draw-window scroll, no hidden first line
    // after a multi-line paste). Then (re)arm the caret follow.
    private void OnTextAreaChanged()
    {
        if (_fieldRect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_fieldRect);
        _followUntilFrame = Time.frameCount + FollowFrames;
    }

    // Per frame (TextArea only). (1) If the field's rect changed size (first real layout, a width change), the label
    // slice InputField computed against the OLD rect is stale — ForceLabelUpdate re-slices. (2) While focused, a
    // caret move or text change arms a short follow window; outside it we never touch the scroll, so a manual wheel
    // scroll while idle isn't fought.
    private void TickTextArea(bool focused)
    {
        if (_field == null || _fieldRect == null || _scroll == null) return;
        var size = _fieldRect.rect.size;
        if (size.x != _lastFieldSize.x || size.y != _lastFieldSize.y) { _lastFieldSize = size; _field.ForceLabelUpdate(); }
        ClipCaret(focused);
        if (!focused) { _lastCaret = -1; return; }
        var caret = _field.caretPosition;
        if (caret != _lastCaret) { _lastCaret = caret; _followUntilFrame = Time.frameCount + FollowFrames; }
        if (Time.frameCount <= _followUntilFrame) FollowCaret(caret);
    }

    // Scroll the minimum needed to bring the caret's line inside the viewport. The line comes from the Text's own
    // TextGenerator (it renders the full text: the field fits it, so the draw window starts at 0 and line char
    // indices == InputField caret indices). Generator coords are PIXELS: ÷ Text.pixelsPerUnit → the Text's local
    // units, exactly as InputField.GenerateCaret maps lines[i].topY / height. GetLinesArray (not .lines: an IList
    // under IL2CPP interop) indexes the same way on both sides. A stale generator (mesh not rebuilt yet) just
    // retries on the next frame of the follow window.
    private void FollowCaret(int caret)
    {
        var txt = _field!.textComponent;
        var vp = _scroll!.viewport;
        if (txt == null || vp == null) return;
        var lines = txt.cachedTextGenerator.GetLinesArray();
        if (lines == null || lines.Length == 0) return;
        var li = 0;
        for (var i = 1; i < lines.Length; i++) { if (lines[i].startCharIdx > caret) break; li = i; }
        var line = lines[li];
        var ppu = txt.pixelsPerUnit > 0f ? txt.pixelsPerUnit : 1f;
        var trt = txt.rectTransform;
        var top = vp.InverseTransformPoint(trt.TransformPoint(new Vector3(0f, line.topY / ppu, 0f))).y;
        var bottom = vp.InverseTransformPoint(trt.TransformPoint(new Vector3(0f, (line.topY - line.height) / ppu, 0f))).y;
        var view = vp.rect;
        // Line above the view → content DOWN (anchoredPosition.y smaller); below → content UP (top-pivot content).
        var delta = top > view.yMax ? view.yMax - top : bottom < view.yMin ? view.yMin - bottom : 0f;
        if (Mathf.Abs(delta) < 0.5f) return;
        var p = _fieldRect!.anchoredPosition;
        p.y = Mathf.Clamp(p.y + delta, 0f, Mathf.Max(0f, _fieldRect.rect.height - view.height));
        _fieldRect.anchoredPosition = p;
        _scroll.velocity = Vector2.zero;
    }

    // Legacy InputField draws the caret AND the selection highlight on its own child "<name> Input Caret" (a raw
    // CanvasRenderer + ignoreLayout LayoutElement, held in the private m_CachedInputRenderer) — NOT a MaskableGraphic /
    // IClippable, so the Viewport's RectMask2D never clips it: after a manual wheel/bar scroll (caret-follow idle) the
    // caret, or a selection on a scrolled-out line, drew over the padding and the window. So clip it ourselves exactly
    // as RectMask2D clips its children: intersect the canvasRect (ROOT-canvas space — the space RectMask2D hands its
    // clippables) of every enabled RectMask2D from the Viewport up (an enclosing window scroll mask included) and pass
    // it to CanvasRenderer.EnableRectClipping. Re-applied every tick once found — the window drags and the canvas
    // rescales — and canvasRect is a native corner transform returning a struct (no managed allocation).
    private void ClipCaret(bool focused)
    {
        if (_caretRenderer == null && (!focused || !FindCaretRenderer())) return;
        var any = false;
        var clip = default(Rect);
        for (var i = 0; i < _clipMasks.Count; i++)
        {
            var m = _clipMasks[i];
            if (m == null || !m.isActiveAndEnabled) continue;
            var r = m.canvasRect;
            clip = any ? Intersect(clip, r) : r;
            any = true;
        }
        if (any) _caretRenderer!.EnableRectClipping(clip);
        else _caretRenderer!.DisableRectClipping();
    }

    // The caret GO doesn't exist until InputField's first geometry pass after focus (parented under the Text's parent
    // = our Field), so this keeps looking each FOCUSED tick until it appears. Matched structurally — a Field child with
    // a CanvasRenderer but no Graphic (the Text child has one) — not by name (no per-frame string marshalling) and not
    // via the private m_CachedInputRenderer (keeps this file pure UnityEngine.UI). The mask chain is fixed once built.
    private bool FindCaretRenderer()
    {
        var t = _fieldRect!;
        for (var i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            if (c.GetComponent<Graphic>() != null) continue;
            var cr = c.GetComponent<CanvasRenderer>();
            if (cr == null) continue;
            _caretRenderer = cr;
            _clipMasks.Clear();
            for (var p = t.parent; p != null; p = p.parent)   // t.parent = the Viewport
            {
                var m = p.GetComponent<RectMask2D>();
                if (m != null) _clipMasks.Add(m);
            }
            return true;
        }
        return false;
    }

    // Overlap of two rects; zero-size when they don't overlap (EnableRectClipping then hides the caret entirely).
    private static Rect Intersect(Rect a, Rect b)
    {
        float xMin = Mathf.Max(a.xMin, b.xMin), yMin = Mathf.Max(a.yMin, b.yMin);
        float xMax = Mathf.Max(xMin, Mathf.Min(a.xMax, b.xMax)), yMax = Mathf.Max(yMin, Mathf.Min(a.yMax, b.yMax));
        return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
    }
}
