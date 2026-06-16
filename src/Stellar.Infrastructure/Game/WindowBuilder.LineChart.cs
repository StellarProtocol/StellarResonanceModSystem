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
    // Builds a fixed-size container that reserves the chart's footprint. The line mesh/raster is added in
    // the renderer task; the zoom control row in a later task.
    private void BuildLineChart(LineChartElement lc, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("LineChart", parent);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = lc.Width; le.preferredHeight = lc.Height;
        le.flexibleWidth = 0f; le.flexibleHeight = 0f;

        // Placeholder plot background (kept behind the lines once the renderer lands).
        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.05f, 0.07f, 0.09f, 1f);
        bg.raycastTarget = true;   // needed later for scroll/drag zoom
    }
}
