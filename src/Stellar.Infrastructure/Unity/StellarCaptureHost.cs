using System;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Injected coroutine host for <see cref="Stellar.Infrastructure.Rendering.UnityFrameGrabber"/>. Lives on its own
/// <c>DontDestroyOnLoad</c> GameObject; has no per-frame message (no managed entry unless a coroutine runs).
/// <see cref="Destroyed"/> lets the grabber fail every in-flight capture instead of leaving it hanging.
/// </summary>
public sealed class StellarCaptureHost : MonoBehaviour
{
    // Required by Il2CppInterop for managed MonoBehaviour subclasses.
    public StellarCaptureHost(IntPtr ptr) : base(ptr) { }

    /// <summary>Invoked (main thread) when Unity destroys this component.</summary>
    internal Action? Destroyed;

    private void OnDestroy()
    {
        try { Destroyed?.Invoke(); }
        catch { /* never let a managed exception cross back into Unity */ }
    }
}
