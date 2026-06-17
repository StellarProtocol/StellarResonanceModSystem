using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// LineChart binding for WindowBuilder — split out of WindowBuilder.Bindings.cs to keep that file under the
// size gate. Polled by WindowToken.Apply() like the other bindings.
internal sealed partial class WindowBuilder
{
    // LineChart re-mesh poll: re-pulls the series set + visible range each Apply, but only re-runs the
    // (closure-supplied) geometry → mesh push when the series LIST REFERENCE, the range, or (FillWidth) the
    // laid-out width actually changed. Archived chart data is static, so steady-state polls are a reference
    // compare + a tuple compare + a float compare and do NO mesh work. Remesh() maps points via ChartGeometry
    // and calls ChartGraphic.SetData (→ dirties mesh).
    internal sealed class ChartBinding
    {
        public Func<IReadOnlyList<ChartSeries>> Series = null!;
        public Func<(float Min, float Max)> Range = null!;
        public Action<IReadOnlyList<ChartSeries>> Remesh = null!;
        // Live chart width: FillWidth charts re-mesh when their laid-out width changes (window resize), in
        // addition to series/range. Null (fixed-width charts) compares as a constant 0 → no extra re-mesh.
        public Func<float>? Width;
        private object? _lastSeries; private bool _init; private (float, float) _lastRange; private float _lastWidth;
        public void Apply()
        {
            var series = Series();
            var range = Range();
            var width = Width?.Invoke() ?? 0f;
            if (_init && ReferenceEquals(series, _lastSeries) && range.Equals(_lastRange)
                && Mathf.Approximately(width, _lastWidth)) return;
            _lastSeries = series; _lastRange = range; _lastWidth = width; _init = true;
            Remesh(series);
        }
    }

    // One reusable legend entry slot: the entry GameObject (toggled active per series presence), its swatch
    // Image, and its name Text. LegendBinding repopulates these from the series set on a reference change.
    internal readonly struct LegendSlot
    {
        public LegendSlot(GameObject root, Image swatch, Text label) { Root = root; Swatch = swatch; Label = label; }
        public GameObject Root { get; }
        public Image Swatch { get; }
        public Text Label { get; }
    }

    // Dynamic legend poll: ref-diffs the series LIST instance each Apply and, only when it changed, repaints the
    // pre-built slot pool — swatch colour + label text + active state per series, surplus slots hidden. Mirrors
    // the ChartBinding ref-diff so a toggled source's legend entry appears/disappears in lockstep with its line,
    // with no per-frame work (steady state is one reference compare) and no GameObject churn on a toggle.
    internal sealed class LegendBinding
    {
        public Func<IReadOnlyList<ChartSeries>> SeriesFn = null!;
        public LegendSlot[] Slots = Array.Empty<LegendSlot>();
        private object? _lastSeries; private bool _init;
        public void Apply()
        {
            var series = SeriesFn();
            if (_init && ReferenceEquals(series, _lastSeries)) return;
            _lastSeries = series; _init = true;
            for (var i = 0; i < Slots.Length; i++)
            {
                var slot = Slots[i];
                if (slot.Root == null) continue;
                if (i < series.Count)
                {
                    var s = series[i];
                    if (slot.Swatch != null) slot.Swatch.color = new Color(s.Color.R, s.Color.G, s.Color.B, s.Color.A);
                    if (slot.Label != null) { slot.Label.text = s.Name; slot.Label.fontStyle = s.Emphasis ? FontStyle.Bold : FontStyle.Normal; }
                    slot.Root.SetActive(true);
                }
                else slot.Root.SetActive(false);
            }
        }
    }
}
