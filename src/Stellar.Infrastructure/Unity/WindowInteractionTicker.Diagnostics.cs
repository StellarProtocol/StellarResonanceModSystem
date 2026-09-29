using Stellar.Abstractions.Diagnostics;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

// [Hitch] render-frame attribution. This ticker's Update runs once per RENDERED frame (unlike the framework tick,
// which is InvokeRepeating at the master rate), so it is where a slow frame is noticed and HitchProbe's per-frame
// buckets are reported + reset. Gated on HitchProbe.IsEnabled (the diagnostics flag or HITCH) — off = one field read.
public sealed partial class WindowInteractionTicker
{
    internal System.Action<string>? HitchLog;
    private bool _il2CppGcUnavailable;

    private void TickHitch()
    {
        if (!HitchProbe.IsEnabled || HitchLog == null) return;
        HitchProbe.OnRenderFrame(Time.unscaledDeltaTime * 1000.0, Il2CppGcCount(), HitchLog);
    }

    // The game's own (IL2CPP / Boehm) collector — distinct from the BepInEx CoreCLR heap System.GC reports.
    private int Il2CppGcCount()
    {
        if (_il2CppGcUnavailable) return -1;
        try { return Il2CppSystem.GC.CollectionCount(0); }
        catch { _il2CppGcUnavailable = true; return -1; }
    }
}
