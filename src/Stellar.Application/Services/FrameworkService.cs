using System;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class FrameworkService : IFramework
{
    // The Update subscribers as a snapshot array, rebuilt on subscribe / unsubscribe (rare) so Tick can run each one on
    // its own without a per-tick allocation: one throwing subscriber is warned once and never starves the ones after it
    // (time-pause review I-1, 2026-10-03 — an unguarded multicast aborted the rest of the tick, and with it the rest of the
    // framework's global-rate work, every tick the thrower threw).
    private readonly object _subsLock = new();
    private Action<float>? _update;
    private Action<float>[] _subs = Array.Empty<Action<float>>();
    private readonly HashSet<Delegate> _warnedSubs = new();

    public event Action<float>? Update
    {
        add { lock (_subsLock) { _update += value; _subs = Snapshot(_update); } }
        remove { lock (_subsLock) { _update -= value; _subs = Snapshot(_update); } }
    }

    /// <summary>Where a throwing <see cref="Update"/> subscriber is reported (once per subscriber). Set by the Host.</summary>
    internal Action<string>? Warn { get; set; }
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

        // Fast path in production: each subscriber on its own over the snapshot array, no per-frame alloc.
        var subs = _subs;
        if (!PerfProbe.IsEnabled)
        {
            foreach (var d in subs) Run(d, deltaTime);
            return;
        }

        // Perf-harness path: invoke each subscriber individually so PerfProbe can
        // attribute the per-frame Update cost to the owning plugin (by namespace).
        foreach (var d in subs)
        {
            // Namespace alone collapses every Host-side per-frame lambda into one "plug:Stellar.Host"
            // bucket — useless when that bucket is the hot one. Append the method name so each delegate
            // (incl. compiler-generated closures like <BuildUGuiAdapters>b__N) gets its own segment and
            // the offending tick is identifiable. Perf-harness path only (gated on PerfProbe.IsEnabled).
            var ns = d.Target?.GetType().Namespace ?? d.Method.DeclaringType?.FullName ?? "?";
            var seg = "plug:" + ns + "::" + (d.Method.DeclaringType?.Name is { } dt ? dt + "." : "") + d.Method.Name;
            PerfProbe.BeginSeg(seg);
            try { Run(d, deltaTime); }
            finally { PerfProbe.EndSeg(seg); }   // seg per-delegate; see comment above
        }
    }

    private void Run(Action<float> d, float deltaTime)
    {
        try { d(deltaTime); }
        catch (Exception ex)
        {
            if (!_warnedSubs.Add(d)) return;
            var who = (d.Method.DeclaringType?.FullName ?? "?") + "." + d.Method.Name;
            Warn?.Invoke($"[Framework] an Update subscriber threw (it keeps being called; the others run): {who}: {ex.Message}");
        }
    }

    private static Action<float>[] Snapshot(Action<float>? multicast)
    {
        if (multicast is null) return Array.Empty<Action<float>>();
        var list = multicast.GetInvocationList();
        var subs = new Action<float>[list.Length];
        for (var i = 0; i < list.Length; i++) subs[i] = (Action<float>)list[i];
        return subs;
    }
}
