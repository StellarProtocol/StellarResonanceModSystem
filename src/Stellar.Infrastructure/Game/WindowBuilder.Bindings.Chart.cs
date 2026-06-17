using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using UnityEngine;

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
}
