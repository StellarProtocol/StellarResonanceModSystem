using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class FrameworkService : IFramework
{
    public event Action<float>? Update;
    public long FrameCount { get; private set; }

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
            var seg = "plug:" + (d.Target?.GetType().Namespace ?? d.Method.DeclaringType?.FullName ?? "?");
            PerfProbe.BeginSeg(seg);
            try { ((Action<float>)d).Invoke(deltaTime); }
            finally { PerfProbe.EndSeg(seg); }
        }
    }
}
