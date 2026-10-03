using System;
using UnityEngine;
namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Per-render-frame driver for the free camera (Update: set the vcam pose before the CinemachineBrain's LateUpdate) and
/// the freeze position hold (LateUpdate). Kept disabled by <see cref="FrameDriverHost"/> unless one of them is live,
/// so it costs nothing otherwise (Unity never calls a disabled behaviour). Member-minimal for ClassInjector.
/// </summary>
public sealed class FreeCameraFrameDriver : MonoBehaviour
{
    // Required by Il2CppInterop for managed MonoBehaviour subclasses.
    public FreeCameraFrameDriver(IntPtr ptr) : base(ptr) { }

    internal Action<float>? OnFrame;
    internal Action? OnLateFrame;

    public void Update()
    {
        var f = OnFrame;
        if (f is null) return;
        try { f(Time.unscaledDeltaTime); }
        catch { /* the callers catch and release; never let an exception cross back into Unity */ }
    }

    public void LateUpdate()
    {
        var l = OnLateFrame;
        if (l is null) return;
        try { l(); }
        catch { /* see Update */ }
    }
}
