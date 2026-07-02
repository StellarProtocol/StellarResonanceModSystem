using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Line-chart pan/zoom gesture handling for <see cref="WindowInteractionTicker"/>. Scroll over a chart plot
/// zooms its visible window around the cursor's time position; left-drag pans it. The window state lives in
/// the plugin (<c>Get</c>/<c>Set</c>) and all bounds math goes through the pure <see cref="ChartWindow"/>
/// helper, so the scroll/drag gestures, the −/+/Reset button bar, and the range scrollbar all converge on
/// one window. Split out of the main ticker file to keep it under the 500-LoC gate; mirrors the RenderHost
/// gesture pattern (claim on mouse-down, drive while held).
/// </summary>
public sealed partial class WindowInteractionTicker
{
    // Line-chart pan/zoom plots: window state lives in the plugin (Get/Set). The non-texture analog of
    // RenderHosts (no per-frame texture bind, just scroll-zoom + drag-pan gestures).
    internal readonly List<ChartPan> ChartPans = new();
    internal struct ChartPan
    {
        public RectTransform Plot;                       // the inner plot hit-rect (cursor X → window time)
        public Func<(float Min, float Max)> Get;         // current plugin-owned window
        public Action<(float Min, float Max)> Set;       // push a new clamped window
        public Func<float> Total;                        // total chart seconds (pan/zoom upper bound)
        public Func<float> MinSpan;                      // minimum visible span (≥ 2 buckets)
        // Cached at build time (GetComponentInParent is too costly per-tick): true when the plot lives inside a
        // ScrollRect viewport. The wheel is read here via legacy Input.mouseScrollDelta, but ScrollRect consumes
        // it via the EventSystem OnScroll — two independent pipelines. If the chart is nested in a scroll, one
        // wheel tick would BOTH zoom the chart and scroll the list, so we suppress the chart-zoom and yield the
        // wheel to the scroll. The drag-pan path is unaffected (left-drag never conflicts with the wheel).
        public bool InsideScrollRect;
    }
    private int _activeChartPan = -1;                    // index into ChartPans of the dragged plot, or -1
    // Scroll-zoom factors. Reciprocal so scroll-in then scroll-out returns to the starting span (0.83 ≈ 1/1.2).
    private const float ScrollZoomInFactor = 0.83f;      // scroll up = zoom in  (shrink span)
    private const float ScrollZoomOutFactor = 1.2f;      // scroll down = zoom out (grow span)

    private void PruneChartPans()
    {
        for (var i = ChartPans.Count - 1; i >= 0; i--) if (ChartPans[i].Plot == null) ChartPans.RemoveAt(i);
    }

    // Scroll over a chart plot → zoom its visible window around the cursor's time position (the cursor's
    // X fraction across the plot maps into the current window). Scroll up = zoom in (×0.83), down = out (×1.2).
    private void TickChartZoom()
    {
        var scroll = Input.mouseScrollDelta.y;
        if (scroll == 0f) return;
        var mp = Input.mousePosition;
        for (var i = 0; i < ChartPans.Count; i++)
        {
            var cp = ChartPans[i];
            if (cp.Plot == null || !cp.Plot.gameObject.activeInHierarchy) continue;
            // Chart nested in a scrolling container: yield the wheel to the ScrollRect (EventSystem OnScroll) so a
            // single tick doesn't both zoom the chart and scroll the list. Cached at build time (no per-tick walk).
            if (cp.InsideScrollRect) continue;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(cp.Plot, mp, null, out var lp)) continue;
            // Reuse the same `lp` (plot-local point) for both the bounds check and the anchor-time math below, so
            // this manual contains-check is intentional, not a duplicate of RectangleContainsScreenPoint.
            var r = cp.Plot.rect;
            if (lp.x < r.xMin || lp.x > r.xMax || lp.y < r.yMin || lp.y > r.yMax) continue;
            if (FrontWindowBlocks(mp, FindWindowRoot(cp.Plot))) continue;
            try
            {
                var w = cp.Get();
                var anchor = w.Min + Mathf.Clamp01((lp.x - r.xMin) / r.width) * (w.Max - w.Min);
                var factor = scroll > 0f ? ScrollZoomInFactor : ScrollZoomOutFactor;
                cp.Set(ChartWindow.ZoomAround(w, anchor, factor, cp.Total(), cp.MinSpan()));
            }
            catch { }
            return;
        }
    }

    // Mouse-down hit-test: the topmost active chart plot under the cursor claims the pan gesture.
    private int HitChartPan(Vector3 mp)
    {
        for (var i = 0; i < ChartPans.Count; i++)
        {
            var plot = ChartPans[i].Plot;
            if (plot == null || !plot.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(plot, mp, null)) continue;
            if (FrontWindowBlocks(mp, FindWindowRoot(plot))) continue;
            return i;
        }
        return -1;
    }

    // Drag over a chart plot → pan its visible window by the cursor's horizontal travel, converted from
    // pixels to seconds via the current span / plot width. anchoredPosition is untouched (the window stays);
    // only the plugin-owned visible range shifts, so the renderer redraws the same plot to the new window.
    private void TickChartPanDrag()
    {
        if (_activeChartPan >= ChartPans.Count) { _activeChartPan = -1; return; }
        var cp = ChartPans[_activeChartPan];
        if (cp.Plot == null) { _activeChartPan = -1; return; }
        var m = (Vector2)Input.mousePosition;
        var d = m - _lastMouse;
        _lastMouse = m;
        try
        {
            var w = cp.Get();
            var width = cp.Plot.rect.width;
            if (width <= 0f) return;
            var perPx = (w.Max - w.Min) / width;
            cp.Set(ChartWindow.Pan(w, -d.x * perPx, cp.Total(), cp.MinSpan()));   // drag right = scroll back in time
        }
        catch { }
    }
}
