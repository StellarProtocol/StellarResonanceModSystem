using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder line-chart element. Builds a fixed-size plot container (dark background) with reserved
/// left/bottom margins for labelled X/Y axes, axis titles, and a legend row beneath. The plotted lines
/// themselves are added by the (deferred) renderer task — this partial produces the container + axes
/// chrome only. All math goes through the Unity-free <see cref="ChartGeometry"/> helper.
/// </summary>
internal sealed partial class WindowBuilder
{
    // Reserved chrome margins (px) around the plot rect: room for the Y title + Y tick labels on the left,
    // the X tick labels + X title under the bottom, and a small breathing gap top/right.
    private const float ChartMarginLeft = 56f;
    private const float ChartMarginBottom = 40f;
    private const float ChartMarginTop = 8f;
    private const float ChartMarginRight = 12f;
    private const float ChartLegendHeight = 22f;
    private const int ChartLabelSize = 11;

    // Builds the fixed-size chart: a plot panel (dark background) with axis tick labels, axis titles, and a
    // legend row beneath. The line geometry is added by the deferred renderer task; raycastTarget on the
    // plot panel is left on so the later scroll/drag zoom can poll it.
    private void BuildLineChart(LineChartElement lc, Transform parent, WindowToken token)
    {
        var root = UGuiPrimitives.NewChild("LineChart", parent);
        var rle = root.AddComponent<LayoutElement>();
        rle.preferredWidth = lc.Width; rle.preferredHeight = lc.Height + ChartLegendHeight;
        rle.flexibleWidth = 0f; rle.flexibleHeight = 0f;

        var plot = AddChartPanel(root.transform, lc.Width, lc.Height);
        var inner = new Rect(ChartMarginLeft, ChartMarginBottom,
            lc.Width - ChartMarginLeft - ChartMarginRight, lc.Height - ChartMarginBottom - ChartMarginTop);

        var yMax = ChartYMax(lc);
        BuildYTicks(plot.transform, lc, inner, yMax, token);
        BuildXTicks(plot.transform, lc, inner, token);
        BuildAxisTitles(plot.transform, lc, inner, token);
        BuildLegend(root.transform, lc, token);
    }

    // The dark plot panel, anchored top-left within the chart root at full chart size (legend sits below it).
    private GameObject AddChartPanel(Transform parent, float width, float height)
    {
        var go = UGuiPrimitives.NewChild("Plot", parent);
        go.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = Vector2.zero; rt.sizeDelta = new Vector2(width, height);
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.05f, 0.07f, 0.09f, 1f);
        bg.raycastTarget = true;   // needed later for scroll/drag zoom
        return go;
    }

    // Auto-scaled (or overridden) Y max for the current visible bucket window.
    private static float ChartYMax(LineChartElement lc)
    {
        if (lc.YMaxOverride?.Invoke() is { } fixedMax && fixedMax > 0f) return fixedMax;
        var (min, max) = lc.VisibleRange();
        var bucket = Mathf.Max(lc.BucketSeconds(), 0.0001f);
        var minBucket = Mathf.FloorToInt(min / bucket);
        var maxBucket = Mathf.CeilToInt(max / bucket);
        return ChartGeometry.NiceYMax(ChartGeometry.VisiblePeak(lc.Series(), minBucket, maxBucket));
    }

    // YTicks+1 right-aligned labels stepping 0..yMax down the left margin, each centred on its gridline row.
    private void BuildYTicks(Transform parent, LineChartElement lc, Rect inner, float yMax, WindowToken token)
    {
        for (var i = 0; i <= lc.YTicks; i++)
        {
            var t = i / (float)lc.YTicks;
            var value = yMax * t;
            var y = inner.y + t * inner.height;   // from bottom up
            var lbl = ChartLabel(parent, TextAnchor.MiddleRight, "YTick", token);
            PlaceLabel(lbl, new Rect(0f, y - 8f, ChartMarginLeft - 6f, 16f));
            token.Texts.Add(new TextBinding { C = lbl, TextFn = () => lc.FormatY(value) });
        }
    }

    // XTicks+1 centred labels across the bottom margin, stepping over the visible time range.
    private void BuildXTicks(Transform parent, LineChartElement lc, Rect inner, WindowToken token)
    {
        for (var i = 0; i <= lc.XTicks; i++)
        {
            var t = i / (float)lc.XTicks;
            var x = inner.x + t * inner.width;
            var lbl = ChartLabel(parent, TextAnchor.MiddleCenter, "XTick", token);
            PlaceLabel(lbl, new Rect(x - 24f, ChartMarginBottom - 22f, 48f, 16f));
            token.Texts.Add(new TextBinding
            {
                C = lbl,
                TextFn = () => { var (min, max) = lc.VisibleRange(); return lc.FormatX(min + t * (max - min)); },
            });
        }
    }

    // Rotated Y title down the far-left edge; centred X title under the X tick labels.
    private void BuildAxisTitles(Transform parent, LineChartElement lc, Rect inner, WindowToken token)
    {
        var yTitle = ChartLabel(parent, TextAnchor.MiddleCenter, "YTitle", token);
        // Rotated +90° (CCW) about the bottom-left pivot: a (width=plotHeight × height=14) rect then occupies
        // x ∈ [pivotX-14, pivotX], y ∈ [pivotY, pivotY+plotHeight] — a vertical band reading bottom-to-top in
        // the far-left margin alongside the Y ticks. Pivot at (14, inner.y) keeps it inside the left margin.
        (yTitle.GetComponent<LayoutElement>() ?? yTitle.gameObject.AddComponent<LayoutElement>()).ignoreLayout = true;
        var yrt = yTitle.rectTransform;
        yrt.anchorMin = yrt.anchorMax = yrt.pivot = new Vector2(0f, 0f);
        yrt.sizeDelta = new Vector2(inner.height, 14f);
        yrt.localRotation = Quaternion.Euler(0f, 0f, 90f);
        yrt.anchoredPosition = new Vector2(14f, inner.y);
        token.Texts.Add(new TextBinding { C = yTitle, TextFn = lc.TitleY });

        var xTitle = ChartLabel(parent, TextAnchor.MiddleCenter, "XTitle", token);
        PlaceLabel(xTitle, new Rect(inner.x, 2f, inner.width, 14f));
        token.Texts.Add(new TextBinding { C = xTitle, TextFn = lc.TitleX });
    }

    // A swatch + name per series, laid out left-to-right in a row beneath the plot panel.
    private void BuildLegend(Transform parent, LineChartElement lc, WindowToken token)
    {
        var row = UGuiPrimitives.NewChild("Legend", parent);
        row.AddComponent<LayoutElement>().ignoreLayout = true;
        var rt = row.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(ChartMarginLeft, -lc.Height - 2f);
        rt.sizeDelta = new Vector2(lc.Width - ChartMarginLeft, ChartLegendHeight);
        UGuiPrimitives.AddLayout(row, gap: 14f, columns: UGuiPrimitives.RowMode);
        foreach (var s in lc.Series()) BuildLegendEntry(row.transform, s, token);
    }

    // One legend entry: a coloured swatch followed by the series name. (token threads label re-skin.)
    private void BuildLegendEntry(Transform parent, ChartSeries s, WindowToken token)
    {
        var entry = UGuiPrimitives.NewChild("Entry", parent);
        UGuiPrimitives.AddLayout(entry, gap: 5f, columns: UGuiPrimitives.RowMode);
        var sw = UGuiPrimitives.NewChild("Swatch", entry.transform);
        var sle = sw.AddComponent<LayoutElement>();
        sle.preferredWidth = sle.preferredHeight = 12f; sle.flexibleWidth = 0f;
        var img = sw.AddComponent<Image>();
        img.color = new Color(s.Color.R, s.Color.G, s.Color.B, s.Color.A); img.raycastTarget = false;
        var name = s.Name;
        var lbl = ChartLabel(entry.transform, TextAnchor.MiddleLeft, "Name", token, bold: s.Emphasis);
        lbl.GetComponent<LayoutElement>().flexibleWidth = 0f;
        token.Texts.Add(new TextBinding { C = lbl, TextFn = () => name });
    }

    // A themed muted-colour chart label Text, registered for re-skin like the window's other text.
    private Text ChartLabel(Transform parent, TextAnchor anchor, string name, WindowToken token, bool bold = false)
    {
        var go = UGuiPrimitives.NewChild(name, parent);
        var txt = go.AddComponent<Text>();
        UGuiPrimitives.ConfigureText(txt, Scaled(ChartLabelSize), anchor, bold: bold);
        ApplyMenuFont(txt);
        txt.color = _assets.MenuMuted;
        go.AddComponent<LayoutElement>();
        RegisterTextReskin(token, txt, ChartLabelSize, muted: true);
        return txt;
    }

    // Position a manually-laid-out label by bottom-left within the plot panel (rect.x = left, rect.y =
    // distance from the panel bottom, rect.width/height = label box). Pivot/anchor are bottom-left.
    private static void PlaceLabel(Text txt, Rect box)
    {
        var le = txt.GetComponent<LayoutElement>() ?? txt.gameObject.AddComponent<LayoutElement>();
        le.ignoreLayout = true;
        var rt = txt.rectTransform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0f);
        rt.sizeDelta = new Vector2(box.width, box.height);
        rt.anchoredPosition = new Vector2(box.x, box.y);
    }
}
