using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class FrameworkService : IFramework
{
    public event Action<float>? Update;
    public long FrameCount { get; private set; }
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }

    // Timing / main-thread marshalling (IFrameworkTiming). The shared framework's posts + timers drain in
    // Tick(); TimeNow is a process stopwatch so it never touches (and never throws from) UnityEngine.Time.
    private readonly FrameDispatch _dispatch = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public void Post(Action action) => _dispatch.Post(action);
    public IDisposable Every(TimeSpan interval, Action action) => _dispatch.Every(interval, action);
    public float TimeNow => (float)_clock.Elapsed.TotalSeconds;

    // UI scaleFactor of the window overlay's CanvasScaler (canvas px per screen px). Fed each global beat
    // from WindowService.CanvasScale via SetCanvasScale. Canvas dims = screen px ÷ scaleFactor. Stellar.Application
    // has NO Unity reference, so rounding uses System.Math.Round (not UnityEngine.Mathf).
    private float _canvasScale = 1f;

    public int CanvasWidth => _canvasScale > 0f ? (int)System.Math.Round(ScreenWidth / _canvasScale) : ScreenWidth;
    public int CanvasHeight => _canvasScale > 0f ? (int)System.Math.Round(ScreenHeight / _canvasScale) : ScreenHeight;

    public int EffectiveUpdateRateHz => Stellar.Abstractions.Diagnostics.PerfControls.UpdateRateHz;
    public IUpdateRateScope RequestUpdateRate(int hz) => InertUpdateRateScope.Instance;

    internal void SetScreen(int width, int height) { ScreenWidth = width; ScreenHeight = height; }
    internal void SetCanvasScale(float scale) { if (scale > 0f) _canvasScale = scale; }

    internal void Tick(float deltaTime)
    {
        FrameCount++;
        _dispatch.Drain(deltaTime);   // run queued Post()s + Every() timers on the tick thread before Update

        // Fast path in production: single multicast invoke, no per-frame alloc.
        if (!PerfProbe.IsEnabled)
        {
            Update?.Invoke(deltaTime);
            return;
        }

        // Perf-harness path: invoke each subscriber individually so PerfProbe can
        // attribute the per-frame Update cost to the owning plugin (by namespace).
        // Same order + same throw semantics as Invoke (no swallow).
        var subs = Update?.GetInvocationList();
        if (subs is null) return;
        foreach (var d in subs)
        {
            // Namespace alone collapses every Host-side per-frame lambda into one "plug:Stellar.Host"
            // bucket — useless when that bucket is the hot one. Append the method name so each delegate
            // (incl. compiler-generated closures like <BuildUGuiAdapters>b__N) gets its own segment and
            // the offending tick is identifiable. Perf-harness path only (gated on PerfProbe.IsEnabled).
            var ns = d.Target?.GetType().Namespace ?? d.Method.DeclaringType?.FullName ?? "?";
            var seg = "plug:" + ns + "::" + (d.Method.DeclaringType?.Name is { } dt ? dt + "." : "") + d.Method.Name;
            PerfProbe.BeginSeg(seg);
            try { ((Action<float>)d).Invoke(deltaTime); }
            finally { PerfProbe.EndSeg(seg); }   // seg per-delegate; see comment above
        }
    }
}
