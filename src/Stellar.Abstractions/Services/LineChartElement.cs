using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>One labelled series in a <see cref="LineChartElement"/>.</summary>
/// <param name="Name">Legend label for the series.</param>
/// <param name="Color">Line colour (also used in the legend swatch).</param>
/// <param name="Values">Per-bucket Y values, oldest bucket first; index i is at time i × bucket-seconds.</param>
/// <param name="Emphasis">Draw thicker (e.g. the team-total line).</param>
public readonly record struct ChartSeries(string Name, ColorRgba Color, IReadOnlyList<float> Values, bool Emphasis = false);

/// <summary>
/// Multi-series time-series line chart with labelled X/Y axes, axis titles, a legend, and an
/// interactive visible-range (zoom/pan) window. The plugin owns the data and the visible window;
/// the framework draws axes, grid, ticks, and lines, auto-scaling Y to the visible window's peak
/// (unless <paramref name="YMaxOverride"/> returns a value). BCL-only: no Unity types in the contract.
/// </summary>
/// <remarks>
/// Scroll-zoom uses the pointer wheel over the plot. If a chart is nested inside a scrolling container, the
/// host detects the enclosing scroll and suppresses chart scroll-zoom for that chart (the wheel scrolls the
/// container instead) — so the −/+/Reset buttons and the range scrollbar remain the zoom controls there.
/// Prefer not to nest a chart inside a scroll viewport if pointer-wheel zoom is wanted. Drag-to-pan is
/// unaffected by nesting.
/// </remarks>
/// <param name="Series">Provider for the current series set; re-pulled on refresh. Return a <b>stable
/// list reference</b> while the underlying data is unchanged (and a new instance when it changes): the
/// renderer diffs this by reference to skip re-meshing, so a provider that allocates a fresh list every
/// call (e.g. a per-call <c>.ToList()</c>) defeats that and re-triangulates on every refresh tick.</param>
/// <param name="BucketSeconds">Seconds represented by one bucket index (X spacing).</param>
/// <param name="FormatY">Formats a Y value for a tick label (e.g. 18000 → "18k").</param>
/// <param name="FormatX">Formats an X value in seconds for a tick label (e.g. 64 → "1:04").</param>
/// <param name="TitleY">Y-axis title (e.g. "Damage / sec"); re-pulled on refresh (metric-aware).</param>
/// <param name="TitleX">X-axis title (e.g. "Encounter time (m:ss)").</param>
/// <param name="VisibleRange">Current visible window in seconds (min,max); the renderer clips/scales to it.</param>
/// <param name="SetVisibleRange">Called by the renderer's scroll/drag to update the window.</param>
/// <param name="Width">Chart width in px.</param>
/// <param name="Height">Chart height in px.</param>
/// <param name="YTicks">Number of Y gridlines/ticks.</param>
/// <param name="XTicks">Number of X ticks.</param>
/// <param name="YMaxOverride">Optional fixed Y max; null/returns-null = auto-scale to visible peak.</param>
/// <param name="ShowNavigator">When true, the framework draws a Highcharts-style navigator strip beneath
/// the plot + legend: a mini full-range (area-filled) overview of the same <paramref name="Series"/> data
/// (ignoring <paramref name="VisibleRange"/>) with a draggable/resizable brush window that controls
/// <paramref name="VisibleRange"/> (drag middle = pan, drag an edge handle = zoom that edge, double-click =
/// reset to full). The navigator replaces the legacy −/+/Reset button bar + range scrollbar; scroll-wheel
/// zoom and drag-pan on the main plot remain. When false (default) the legacy control strip is drawn.</param>
public sealed record LineChartElement(
    Func<IReadOnlyList<ChartSeries>> Series,
    Func<float> BucketSeconds,
    Func<float, string> FormatY,
    Func<float, string> FormatX,
    Func<string> TitleY,
    Func<string> TitleX,
    Func<(float Min, float Max)> VisibleRange,
    Action<(float Min, float Max)> SetVisibleRange,
    float Width,
    float Height,
    int YTicks = 4,
    int XTicks = 4,
    Func<float?>? YMaxOverride = null,
    bool ShowNavigator = false) : HudElement;
