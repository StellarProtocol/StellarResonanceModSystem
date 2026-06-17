using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// WindowBuilder line-chart FILL-WIDTH reflow (<see cref="LineChartElement.FillWidth"/>). The plot panel +
/// line graphic stretch with the chart root via anchors, and the line/axis/grid mesh re-meshes from the live
/// width in its <c>ChartBinding</c> (<c>.LineChart.cs</c>). This partial repositions the width-DEPENDENT
/// manually-placed chrome — the X-tick labels, the X-axis title, and the navigator strip/overview/brush —
/// when the laid-out width changes, so they track the lines instead of staying at their build-time width.
/// Width-INDEPENDENT chrome (Y ticks, Y title, legend, all left-anchored) needs no reflow. A single poll
/// (registered as a <c>HoverBinding</c>) diffs the live width and fans out to the collected reflow closures,
/// so steady state is one float compare; null in the Mono sandbox path is irrelevant (the poll is local).
/// </summary>
internal sealed partial class WindowBuilder
{
    // Collector threaded through a FillWidth chart build: each width-dependent placement registers a closure
    // here, and RegisterFillWidthReflow wires one width-diffed poll that replays them on resize. A field (not
    // a param on every Build* signature) to stay clear of the 5-param analyzer gate; non-null only between the
    // start and end of a single FillWidth BuildLineChart (charts aren't built re-entrantly within a window).
    private List<Action<float>>? _chartReflow;

    // The plot RectTransform whose laid-out width drives ChartWidth for the chart currently being built under
    // FillWidth. Set at the start of BuildLineChart (FillWidth only), read by the navigator's width-aware
    // remesh, cleared by RegisterFillWidthReflow. Charts aren't built re-entrantly within one window.
    private RectTransform _fillPlotRect = null!;

    // Register the width-diffed reflow poll. Captures the closures collected during the chart sub-builds and
    // replays them whenever ChartWidth changes. The line mesh reflows via its own ChartBinding.Width diff.
    private void RegisterFillWidthReflow(LineChartElement lc, RectTransform plotRect, WindowToken token)
    {
        var reflows = _chartReflow;
        _chartReflow = null;          // close the collection window for this chart
        _fillPlotRect = null!;        // release the per-chart plot-rect reference
        if (reflows == null || reflows.Count == 0) return;
        var lastWidth = float.NaN;
        token.Hovers.Add(new HoverBinding
        {
            Poll = () =>
            {
                var w = ChartWidth(lc, plotRect);
                if (Mathf.Approximately(w, lastWidth)) return;
                lastWidth = w;
                for (var i = 0; i < reflows.Count; i++) reflows[i](w);
            },
        });
    }

    // Record a reflow closure (no-op unless we're inside a FillWidth chart build).
    private void AddChartReflow(Action<float> reflow) => _chartReflow?.Add(reflow);
}
