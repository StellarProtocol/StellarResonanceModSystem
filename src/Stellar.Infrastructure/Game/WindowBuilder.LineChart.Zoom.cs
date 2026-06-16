using System;
using UnityEngine;
using UnityEngine.UI;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder line-chart zoom controls (Task 7, option C). Three input paths — scroll/drag on the plot,
/// a −/+/Reset button bar, and a draggable range scrollbar — all mutate the SINGLE plugin-owned visible
/// window via <see cref="LineChartElement.SetVisibleRange"/>, so they stay in sync (the next window
/// <c>Apply()</c> re-pulls <see cref="LineChartElement.VisibleRange"/> for every binding, re-meshing the
/// lines + relabelling the axes). All window math is the pure <see cref="ChartWindow"/> helper. Split out of
/// <c>WindowBuilder.LineChart.cs</c> to keep that file under the 500-LoC gate. Sandbox-pure: the scroll/drag
/// gesture is ticker-driven (null in the Mono sandbox → static), the buttons + scrollbar render either way.
/// </summary>
internal sealed partial class WindowBuilder
{
    private const float ChartZoomStep = 0.7f;   // −/+ button span scale (·0.7 in, ·1/0.7≈1.43 out)

    // Total chart seconds for the current series set (pan/zoom upper bound).
    private static float ChartTotal(LineChartElement lc) => ChartWindow.TotalSeconds(lc.Series(), lc.BucketSeconds());

    // Minimum visible span (≥ two buckets, capped to total).
    private static float ChartMinSpan(LineChartElement lc) => ChartWindow.MinSpan(lc.BucketSeconds(), ChartTotal(lc));

    // Register the plot rect for ticker-driven scroll-zoom + drag-pan, then build the control strip
    // (−/+/Reset + range scrollbar) beneath the legend.
    private void BuildChartControls(Transform root, GameObject plot, LineChartElement lc, Rect inner, WindowToken token)
    {
        // The pan/zoom gesture maps the cursor's X across the INNER plot rect (the lines' draw area, excluding
        // the axis margins) to a window time. Register a transparent hit-rect sized exactly to `inner` so the
        // cursor→time mapping the gesture handler does (fraction across the rect) matches the rendered lines.
        var hit = UGuiPrimitives.NewChild("PlotHit", plot.transform);
        hit.AddComponent<LayoutElement>().ignoreLayout = true;
        var hrt = hit.GetComponent<RectTransform>();
        hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0f, 0f);
        hrt.sizeDelta = new Vector2(inner.width, inner.height);
        hrt.anchoredPosition = new Vector2(inner.x, inner.y);
        var hitImg = hit.AddComponent<Image>();
        hitImg.color = new Color(0f, 0f, 0f, 0f); hitImg.raycastTarget = true;   // transparent gesture catcher
        RegisterChartPan?.Invoke(hrt, lc.VisibleRange, lc.SetVisibleRange, () => ChartTotal(lc), () => ChartMinSpan(lc));

        var strip = UGuiPrimitives.NewChild("ChartControls", root);
        strip.AddComponent<LayoutElement>().ignoreLayout = true;
        var srt = strip.GetComponent<RectTransform>();
        srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0f, 1f);
        srt.anchoredPosition = new Vector2(ChartMarginLeft, -lc.Height - ChartLegendHeight - 2f);
        srt.sizeDelta = new Vector2(lc.Width - ChartMarginLeft - ChartMarginRight, ChartControlHeight);
        UGuiPrimitives.AddLayout(strip, gap: 6f, columns: UGuiPrimitives.RowMode);

        BuildButton(new ButtonElement(() => "−", () => ZoomChart(lc, ChartZoomStep), Width: 24f), strip.transform, token);
        BuildButton(new ButtonElement(() => "+", () => ZoomChart(lc, 1f / ChartZoomStep), Width: 24f), strip.transform, token);
        BuildButton(new ButtonElement(() => "Reset", () => lc.SetVisibleRange(ChartWindow.Full(ChartTotal(lc)))),
            strip.transform, token);
        BuildRangeScrollbar(strip.transform, lc, token);
    }

    // −/+ button: zoom the window about its centre by `factor`, then push the clamped result.
    private static void ZoomChart(LineChartElement lc, float factor)
        => lc.SetVisibleRange(ChartWindow.ZoomCentre(lc.VisibleRange(), factor, ChartTotal(lc), ChartMinSpan(lc)));

    // A horizontal uGUI Scrollbar that mirrors the visible window: size = span/total, value = window-centre
    // fraction. Dragging it pans; resizing the thumb (size handle) zooms. Both route to SetVisibleRange. A
    // per-apply sync (registered as a HoverBinding Poll) re-reads the window so scroll/drag/buttons keep the
    // bar in step — guarded by a re-entrancy flag so the sync write doesn't re-fire onValueChanged.
    private void BuildRangeScrollbar(Transform parent, LineChartElement lc, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("RangeScrollbar", parent);
        var le = go.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 12f; le.flexibleWidth = 1f; le.minWidth = 60f;
        if (_assets.Capsule != null) { var bg = go.AddComponent<Image>(); bg.sprite = _assets.Capsule; bg.type = Image.Type.Sliced; bg.color = _assets.MenuBorder; }

        var sb = go.AddComponent<Scrollbar>(); sb.direction = Scrollbar.Direction.LeftToRight;
        var handle = UGuiPrimitives.NewChild("Handle", go.transform);
        var hrt = handle.GetComponent<RectTransform>();
        hrt.anchorMin = Vector2.zero; hrt.anchorMax = Vector2.one; hrt.sizeDelta = Vector2.zero; hrt.anchoredPosition = Vector2.zero;
        var thumb = handle.AddComponent<Image>();
        if (_assets.Capsule != null) { thumb.sprite = _assets.Capsule; thumb.type = Image.Type.Sliced; }
        thumb.color = new Color(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 0.75f);
        sb.targetGraphic = thumb; sb.handleRect = hrt; sb.transition = Selectable.Transition.None;

        SyncRangeScrollbar(sb, lc);   // seed the thumb to the initial window
        var syncing = false;
        sb.onValueChanged.AddListener((UnityEngine.Events.UnityAction<float>)(_ => { if (!syncing) PushScrollbarWindow(sb, lc); }));
        token.Hovers.Add(new HoverBinding { Poll = () => { syncing = true; try { SyncRangeScrollbar(sb, lc); } finally { syncing = false; } } });
        RegisterScrollbar?.Invoke(go.GetComponent<RectTransform>());   // drag-exclusion (don't move the window)
    }

    // Map the scrollbar's size (thumb width) + value (0..1 left-edge position) to a visible window. uGUI's
    // horizontal Scrollbar value is the fraction the LEFT edge has travelled across the (1-size) track.
    private static void PushScrollbarWindow(Scrollbar sb, LineChartElement lc)
    {
        var total = ChartTotal(lc);
        var minSpan = ChartMinSpan(lc);
        var span = Mathf.Clamp(sb.size, 0f, 1f) * total;
        if (span < minSpan) span = minSpan;
        var min = Mathf.Clamp01(sb.value) * Mathf.Max(total - span, 0f);
        lc.SetVisibleRange(ChartWindow.Clamp((min, min + span), total, minSpan));
    }

    // Reflect the current plugin-owned window back onto the scrollbar (size + value) without firing its
    // change event — the caller wraps this in the re-entrancy guard.
    private static void SyncRangeScrollbar(Scrollbar sb, LineChartElement lc)
    {
        var total = ChartTotal(lc);
        if (total <= 0f) return;
        var (min, max) = lc.VisibleRange();
        var span = Mathf.Clamp(max - min, 0f, total);
        var size = Mathf.Clamp01(span / total);
        var slack = total - span;
        var value = slack > 0f ? Mathf.Clamp01(min / slack) : 0f;
        if (Math.Abs(sb.size - size) > 0.0005f) sb.size = size;
        if (Math.Abs(sb.value - value) > 0.0005f) sb.value = value;
    }
}
