using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder line-chart NAVIGATOR (Part 4) — a Highcharts-style overview + brush that replaces the
/// legacy −/+/Reset bar + range scrollbar (<c>WindowBuilder.LineChart.Zoom.cs</c>). Below the plot + legend
/// it draws a mini, axis-less, area-filled render of the chart's <b>full</b> data extent (NOT
/// <see cref="LineChartElement.VisibleRange"/>), overlaid with a translucent brush window (two edge handles +
/// a draggable middle) that maps to the plugin-owned visible range: drag the middle to pan, drag a handle to
/// zoom that edge, double-click to reset to full. The brush also reflects <c>VisibleRange()</c> each refresh
/// (bidirectional) under the same <c>syncing</c> re-entrancy guard the scrollbar used. All window math is the
/// pure <see cref="ChartWindow"/> / <see cref="ChartGeometry"/> helpers. Drag input rides the shared ticker
/// via <see cref="RegisterChartNav"/> (null in the Mono sandbox → the strip renders statically, no live drag).
/// </summary>
internal sealed partial class WindowBuilder
{
    private const float ChartNavHeight = 48f;        // navigator strip height (mini chart + brush)
    private const float ChartNavHandleW = 6f;        // resize-handle hit/draw width (px) per edge
    private const float ChartNavGapTop = 6f;         // gap between the legend row and the navigator strip
    private const float ChartNavBrushFillAlpha = 0x22 / 255f;   // brush fill alpha (mockup #5fb0ff22 → ~0.13)

    // Brush palette: the theme accent (tinted) for the fill, full-opacity accent for the edge handles, read
    // from _assets at build so the navigator follows the active/custom theme (mirrors the scrollbar thumb in
    // WindowBuilder.LineChart.Zoom.cs). Computed where the brush parts are built, not as static consts.
    private Color ChartNavBrushFill =>
        new(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, ChartNavBrushFillAlpha);

    private Color ChartNavBrushEdge =>
        new(_assets.MenuAccent.r, _assets.MenuAccent.g, _assets.MenuAccent.b, 1f);

    // Build the navigator strip in place of BuildChartControls. Registers the main plot for scroll/drag-pan
    // (kept), meshes the full-range overview, lays out the brush rects, wires the reflect-sync, and registers
    // the brush for ticker-driven drag.
    private void BuildNavigator(Transform root, GameObject plot, LineChartElement lc, Rect inner, WindowToken token)
    {
        RegisterMainPlotPan(plot, lc, inner, token);

        var strip = UGuiPrimitives.NewChild("ChartNavigator", root);
        strip.AddComponent<LayoutElement>().ignoreLayout = true;
        var srt = strip.GetComponent<RectTransform>();
        var navW = NavWidth(lc, lc.Width);
        if (lc.FillWidth)
        {
            // Span [ChartMarginLeft, width-ChartMarginRight]: left+right inset via offsets, top-anchored band.
            srt.anchorMin = new Vector2(0f, 1f); srt.anchorMax = new Vector2(1f, 1f); srt.pivot = new Vector2(0f, 1f);
            srt.anchoredPosition = new Vector2(ChartMarginLeft, -lc.Height - ChartLegendHeight - ChartNavGapTop);
            srt.sizeDelta = new Vector2(-(ChartMarginLeft + ChartMarginRight), ChartNavHeight);
        }
        else
        {
            srt.anchorMin = srt.anchorMax = srt.pivot = new Vector2(0f, 1f);
            srt.anchoredPosition = new Vector2(ChartMarginLeft, -lc.Height - ChartLegendHeight - ChartNavGapTop);
            srt.sizeDelta = new Vector2(navW, ChartNavHeight);
        }

        var bg = strip.AddComponent<Image>();
        bg.color = NavStripBg(); bg.raycastTarget = true;   // faint lane backdrop (delimits the strip) + double-click catcher

        var navInner = NavInner(lc, lc.Width);   // small top/bottom padding inside the strip
        BuildNavigatorGraphic(strip.transform, lc, navInner, token);
        var brush = BuildBrushRects(strip.GetComponent<RectTransform>(), navInner, lc.FillWidth);
        WireBrush(brush, lc, token);
    }

    // The navigator strip width / inner rect for a given chart width (chart width minus the L/R axis margins).
    private static float NavWidth(LineChartElement lc, float width) => width - ChartMarginLeft - ChartMarginRight;
    private static Rect NavInner(LineChartElement lc, float width) => new(0f, 4f, NavWidth(lc, width), ChartNavHeight - 8f);

    // The transparent inner hit-rect for the MAIN plot's scroll-zoom + drag-pan (unchanged from the legacy
    // control path — the navigator only replaces the button bar/scrollbar, not the in-plot gestures).
    private void RegisterMainPlotPan(GameObject plot, LineChartElement lc, Rect inner, WindowToken token)
    {
        var hit = UGuiPrimitives.NewChild("PlotHit", plot.transform);
        hit.AddComponent<LayoutElement>().ignoreLayout = true;
        var hrt = hit.GetComponent<RectTransform>();
        if (lc.FillWidth)
        {
            // Stretch horizontally with the plot, inset by the L/R axis margins; fixed bottom band height.
            hrt.anchorMin = new Vector2(0f, 0f); hrt.anchorMax = new Vector2(1f, 0f); hrt.pivot = new Vector2(0f, 0f);
            hrt.sizeDelta = new Vector2(-(ChartMarginLeft + ChartMarginRight), inner.height);
            hrt.anchoredPosition = new Vector2(inner.x, inner.y);
        }
        else
        {
            hrt.anchorMin = hrt.anchorMax = hrt.pivot = new Vector2(0f, 0f);
            hrt.sizeDelta = new Vector2(inner.width, inner.height);
            hrt.anchoredPosition = new Vector2(inner.x, inner.y);
        }
        var img = hit.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0f); img.raycastTarget = true;
        RegisterChartPan?.Invoke(hrt, lc.VisibleRange, lc.SetVisibleRange, () => ChartTotal(lc), () => ChartMinSpan(lc));
    }

    // Mesh the full-range overview: an area-filled team-total (emphasis) shape plus faint lines for the rest,
    // mapped over the WHOLE extent (every bucket), ignoring VisibleRange. Re-meshes only on series change.
    private void BuildNavigatorGraphic(Transform parent, LineChartElement lc, Rect navInner, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("NavLines", parent);
        go.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = go.GetComponent<RectTransform>();
        if (lc.FillWidth)
        {
            rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f); rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = Vector2.zero; rt.sizeDelta = new Vector2(0f, ChartNavHeight);
        }
        else
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = Vector2.zero; rt.sizeDelta = new Vector2(navInner.width, ChartNavHeight);
        }
        var graphic = go.AddComponent<ChartGraphic>();
        graphic.raycastTarget = false;
        var plotRect = lc.FillWidth ? _fillPlotRect : null;   // capture locally (the field is cleared post-build)
        token.Charts.Add(new ChartBinding
        {
            Series = lc.Series,
            Range = () => (0f, 0f),   // overview is range-independent: re-mesh only on series change, not pan
            // Under FillWidth the overview also re-meshes on width change (live nav strip width).
            Width = plotRect != null ? () => ChartWidth(lc, plotRect) : (Func<float>?)null,
            Remesh = series =>
            {
                var ni = plotRect != null ? NavInner(lc, ChartWidth(lc, plotRect)) : navInner;
                graphic.SetData(Array.Empty<ChartGraphic.Segment>(), MapNavLines(series, ni), MapNavFill(series, ni));
            },
        });
    }

    // Full-range polylines for every series (faint), so the overview shows the whole shape at a glance.
    private List<ChartGraphic.Polyline> MapNavLines(IReadOnlyList<ChartSeries> series, Rect navInner)
    {
        var len = MaxSeriesLength(series);
        var yMax = ChartGeometry.NiceYMax(ChartGeometry.VisiblePeak(series, 0, Mathf.Max(len - 1, 0)));
        var lines = new List<ChartGraphic.Polyline>(series.Count);
        foreach (var s in series)
        {
            var pts = NavPoints(s, len, yMax, navInner);
            var a = s.Emphasis ? (byte)255 : (byte)110;   // non-emphasis lines drawn faint
            var c = new Color32((byte)(s.Color.R * 255), (byte)(s.Color.G * 255), (byte)(s.Color.B * 255), a);
            lines.Add(new ChartGraphic.Polyline(pts, c, s.Emphasis ? ChartEmphasisWidth : ChartGridWidth));
        }
        return lines;
    }

    // Area fill under the emphasis (team-total) series — the overview's headline shape. Falls back to the
    // first series when none is emphasised so a single-series chart still gets a filled overview.
    private List<ChartGraphic.Fill> MapNavFill(IReadOnlyList<ChartSeries> series, Rect navInner)
    {
        var fills = new List<ChartGraphic.Fill>(1);
        var pick = PickOverviewSeries(series);
        if (pick is { } s)
        {
            var len = MaxSeriesLength(series);
            var yMax = ChartGeometry.NiceYMax(ChartGeometry.VisiblePeak(series, 0, Mathf.Max(len - 1, 0)));
            var pts = NavPoints(s, len, yMax, navInner);
            var fillCol = new Color32((byte)(s.Color.R * 255), (byte)(s.Color.G * 255), (byte)(s.Color.B * 255), 64);
            fills.Add(new ChartGraphic.Fill(pts, navInner.y, fillCol));
        }
        return fills;
    }

    // The overview shape series: the emphasised (team-total) line if any, else the first series.
    private static ChartSeries? PickOverviewSeries(IReadOnlyList<ChartSeries> series)
    {
        foreach (var s in series) if (s.Emphasis) return s;
        return series.Count > 0 ? series[0] : null;
    }

    // Map one series' full bucket range to navigator-local pixels (bucket 0 → x0, last bucket → xMax).
    private static List<Vector2> NavPoints(ChartSeries s, int len, float yMax, Rect navInner)
    {
        var pts = new List<Vector2>(s.Values.Count);
        var lastBucket = Mathf.Max(len - 1, 0);
        for (var b = 0; b < s.Values.Count; b++)
        {
            var x = ChartGeometry.BucketToX(b, 0, lastBucket, navInner.x, navInner.xMax);
            var y = ChartGeometry.ValueToY(s.Values[b], yMax, navInner.y, navInner.yMax);
            pts.Add(new Vector2(x, y));
        }
        return pts;
    }

    // Lay out the brush overlay: a transparent nav hit-rect (cursor→time, bottom-left so its local rect spans
    // [0,width]×[0,height] matching navInner), a translucent fill rect spanning the current window, and two
    // edge handles. The fill/handles are repositioned each refresh by ReflectBrush; here they get initial geom.
    private BrushRects BuildBrushRects(Transform parent, Rect navInner, bool fillWidth)
    {
        var nav = NewBrushPart("NavHit", parent, new Color(0f, 0f, 0f, 0f), navInner.height);
        if (fillWidth)
        {
            // The cursor→time hit-rect stretches with the strip (its local rect then spans [0, liveWidth]).
            nav.anchorMin = new Vector2(0f, 0f); nav.anchorMax = new Vector2(1f, 0f);
            nav.sizeDelta = new Vector2(0f, navInner.height);
            nav.anchoredPosition = new Vector2(navInner.x, navInner.y);
        }
        else
        {
            nav.sizeDelta = new Vector2(navInner.width, navInner.height);
            nav.anchoredPosition = new Vector2(navInner.x, navInner.y);
        }
        var fill = NewBrushPart("BrushFill", parent, ChartNavBrushFill, navInner.height);
        var left = NewBrushPart("BrushLeft", parent, ChartNavBrushEdge, navInner.height);
        var right = NewBrushPart("BrushRight", parent, ChartNavBrushEdge, navInner.height);
        return new BrushRects(navInner, nav, fill, left, right);
    }

    // One brush sub-rect: bottom-left anchored within the navigator strip, raycast on (the ticker hit-tests it).
    private static RectTransform NewBrushPart(string name, Transform parent, Color color, float height)
    {
        var go = UGuiPrimitives.NewChild(name, parent);
        go.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0f);
        rt.sizeDelta = new Vector2(1f, height);
        rt.anchoredPosition = new Vector2(0f, 0f);
        var img = go.AddComponent<Image>();
        img.color = color; img.raycastTarget = true;
        return rt;
    }

    // Wire the reflect-sync + register the brush for ticker-driven drag. The reflect poll only WRITES the
    // brush geometry from VisibleRange() (it never reads the rects back), so unlike the old scrollbar there's
    // no onValueChanged feedback to guard against — the drag handlers read the cursor, not the rects.
    private void WireBrush(BrushRects brush, LineChartElement lc, WindowToken token)
    {
        // Under FillWidth the navigator inner rect tracks the live strip width; otherwise it's the fixed build
        // rect. The brush fill/handles are bottom-left anchored, so ReflectBrush re-places them within ni.
        var plotRect = lc.FillWidth ? _fillPlotRect : null;
        Rect LiveInner() => plotRect != null ? NavInner(lc, ChartWidth(lc, plotRect)) : brush.Inner;
        ReflectBrush(brush, lc, LiveInner());   // seed initial geometry
        token.Hovers.Add(new HoverBinding { Poll = () => ReflectBrush(brush, lc, LiveInner()) });
        RegisterChartNav?.Invoke(new ChartNavReg
        {
            Nav = brush.Nav, Left = brush.Left, Right = brush.Right, Body = brush.Fill,
            Get = lc.VisibleRange, Set = lc.SetVisibleRange,
            Total = () => ChartTotal(lc), MinSpan = () => ChartMinSpan(lc),
        });
    }

    // Reposition the brush fill + handles to the current window, mapped through the navigator's full extent.
    // No-op-cheap when total is 0. Called both at build and per-apply (under the ticker's syncing guard owner
    // — here it only WRITES geometry, never reads back, so it can't feed the drag handlers). ni is the live
    // navigator inner rect (tracks the strip width under FillWidth).
    private static void ReflectBrush(BrushRects brush, LineChartElement lc, Rect ni)
    {
        if (brush.Fill == null || brush.Left == null || brush.Right == null) return;
        var total = ChartTotal(lc);
        var (min, max) = lc.VisibleRange();
        var xL = ChartGeometry.TimeToNavX(min, total, ni.x, ni.xMax);
        var xR = ChartGeometry.TimeToNavX(max, total, ni.x, ni.xMax);
        if (xR < xL + 2f) xR = xL + 2f;
        brush.Fill.anchoredPosition = new Vector2(xL, ni.y);
        brush.Fill.sizeDelta = new Vector2(xR - xL, ni.height);
        brush.Left.anchoredPosition = new Vector2(xL - ChartNavHandleW * 0.5f, ni.y);
        brush.Left.sizeDelta = new Vector2(ChartNavHandleW, ni.height);
        brush.Right.anchoredPosition = new Vector2(xR - ChartNavHandleW * 0.5f, ni.y);
        brush.Right.sizeDelta = new Vector2(ChartNavHandleW, ni.height);
    }

    // The brush transforms + the navigator inner rect they map within. Nav is the transparent cursor→time
    // hit-rect; Fill/Left/Right are the visible brush window + edge handles.
    private readonly struct BrushRects
    {
        public BrushRects(Rect inner, RectTransform nav, RectTransform fill, RectTransform left, RectTransform right)
        {
            Inner = inner; Nav = nav; Fill = fill; Left = left; Right = right;
        }

        public Rect Inner { get; }
        public RectTransform Nav { get; }
        public RectTransform Fill { get; }
        public RectTransform Left { get; }
        public RectTransform Right { get; }
    }
}
