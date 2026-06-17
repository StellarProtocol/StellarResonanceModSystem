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

    internal void SetScreen(int width, int height) { ScreenWidth = width; ScreenHeight = height; }

    internal void Tick(float deltaTime)
    {
        FrameCount++;

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
