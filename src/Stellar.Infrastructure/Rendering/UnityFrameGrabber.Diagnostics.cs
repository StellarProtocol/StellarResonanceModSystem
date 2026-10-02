using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Stellar.Abstractions.Diagnostics;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary>StellarDiagnostics-gated capture logging.</summary>
internal sealed partial class UnityFrameGrabber
{
    private void OnReadback(string path, int bytes, int expected)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoCapture] readback via {path}: {bytes} bytes (expected {expected})");
    }

    private void OnPumpStopped(int livePumps)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoCapture] resume served on thread {System.Environment.CurrentManagedThreadId}; pump stopped, live pumps={livePumps}");
    }

    private void OnTickDrain(int completed)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoCapture] tick drained {completed} late resume(s) on thread {System.Environment.CurrentManagedThreadId}");
    }

    /// <summary>A shaped capture's lens override was just put back: what was set, what the camera reads now, and — one
    /// frame later — that the camera still has the window's aspect (in-game proof of the restore).</summary>
    private void OnLensRestored(Camera cam, CaptureLensOverride lens)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoCapture] lens set aspect={lens.TargetAspect:F4} fov={lens.TargetFieldOfView:F2} (was aspect={lens.PreviousAspect:F4}); " +
                  $"restored aspect={cam.aspect:F4} fov={cam.fieldOfView:F2} screen={ScreenAspect():F4} physical={cam.usePhysicalProperties}");
        _host?.StartCoroutine(LensNextFrame(cam).WrapToIl2Cpp());
    }

    private IEnumerator LensNextFrame(Camera cam)
    {
        var frame = Time.frameCount;
        yield return null;
        if (cam == null) yield break;
        var ok = System.Math.Abs(cam.aspect - ScreenAspect()) < 1e-3f;
        _log.Info($"[PhotoCapture] lens next-frame ok={ok} aspect={cam.aspect:F4} fov={cam.fieldOfView:F2} screen={ScreenAspect():F4} frames={Time.frameCount - frame}");
    }

    private static float ScreenAspect() => Screen.height > 0 ? (float)Screen.width / Screen.height : 0f;
}
