using System;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// The framework tick's driver while the game's clock is paused (the scene freeze's <c>Time.timeScale = 0</c>), when
/// <see cref="StellarTicker"/>'s <c>InvokeRepeating</c> never fires. Kept DISABLED otherwise, so it costs nothing (Unity never
/// calls a disabled behaviour's <c>Update</c>) and the per-frame managed-entry tax the scheduled ticker avoids is paid only
/// while paused. The pacing (real-time interval, one tick per frame across both drivers) is <see cref="TickPacer"/>'s.
/// <see cref="OnGone"/> fires if Unity destroys it (the framework's GameObject going away) so a pause never outlives it.
/// Member-minimal for ClassInjector.
/// </summary>
public sealed class StellarPausedTicker : MonoBehaviour
{
    // Required by Il2CppInterop for managed MonoBehaviour subclasses.
    public StellarPausedTicker(IntPtr ptr) : base(ptr) { }

    internal Action? OnFrame;
    internal Action? OnGone;

    public void Update()
    {
        var f = OnFrame;
        if (f is null) return;
        try { f(); }
        catch { /* the host logs; never let an exception cross back into Unity */ }
    }

    public void OnDestroy()
    {
        try { OnGone?.Invoke(); }
        catch { /* see Update */ }
    }
}
