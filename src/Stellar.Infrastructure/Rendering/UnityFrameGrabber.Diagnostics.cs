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
    /// frame later — that the camera's whole lens (aspect, view angle, physical mode, gate fit, focal length) is the one
    /// the player had before the shot (in-game proof of the restore). Never throws: it runs after the capture's cleanup.</summary>
    private void OnLensRestored(Camera cam, CaptureLensOverride lens)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        try
        {
            var was = lens.Previous;
            var plan = lens.Plan;
            _log.Info($"[PhotoCapture] lens set aspect={plan.Aspect:F4} angle={plan.VerticalAngle:F2} fit={plan.GateFit} focal={plan.FocalLength:F3} " +
                      $"(was aspect={was.Aspect:F4} fov={was.FieldOfView:F2} physical={was.Physical} fit={was.GateFit} focal={was.FocalLength:F3}); " +
                      $"restored aspect={cam.aspect:F4} fov={cam.fieldOfView:F2} fit={cam.gateFit} focal={cam.focalLength:F3} screen={ScreenAspect():F4}");
            _host?.StartCoroutine(LensNextFrame(cam, was).WrapToIl2Cpp());
        }
        catch (System.Exception ex)
        {
            _log.Warning($"[PhotoCapture] lens diagnostics failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private IEnumerator LensNextFrame(Camera cam, LensState was)
    {
        var frame = Time.frameCount;
        yield return null;
        if (cam == null) yield break;
        var now = CaptureLensOverride.Read(new UnityCaptureLens(cam));
        var ok = CaptureLensPlanner.SameLens(was, now);   // an automatic aspect reads the window's again
        _log.Info($"[PhotoCapture] lens next-frame ok={ok} aspect={now.Aspect:F4} fov={now.FieldOfView:F2} physical={now.Physical} " +
                  $"fit={now.GateFit} focal={now.FocalLength:F3} screen={ScreenAspect():F4} frames={Time.frameCount - frame}");
    }

    private static float ScreenAspect() => Screen.height > 0 ? (float)Screen.width / Screen.height : 0f;
}
