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

    /// <summary>One line per ReShade capture: the bridge's raw LastRender code where the warm-up ended and after the real
    /// render (-1 no runtime, -2 nothing queued, -3 view creation failed, else techniques drawn), plus why it ended.</summary>
    private void OnReShadeCapture(Stellar.Application.Services.ReShadeCapturePlanner planner, int renderCode, bool applied, long elapsedMs,
        Stellar.Abstractions.Domain.CaptureSize size)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var render = renderCode == NoRealRender ? "none" : renderCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _log.Info($"[PhotoCapture] reshade applied={applied} reason={ReShadeReason(planner.Outcome, renderCode)} " +
                  $"warmUpEndCode={planner.WarmUpEndCode} renderCode={render} retries={planner.Retries} " +
                  $"size={size.Width}x{size.Height} elapsedMs={elapsedMs}");
    }

    private static string ReShadeReason(Stellar.Application.Services.ReShadeWarmUpOutcome outcome, int renderCode) => outcome switch
    {
        Stellar.Application.Services.ReShadeWarmUpOutcome.Drew => renderCode > 0 ? "drew" : "warm-up-drew",
        Stellar.Application.Services.ReShadeWarmUpOutcome.TimedOut => "timeout",
        Stellar.Application.Services.ReShadeWarmUpOutcome.Error => "error-code",
        Stellar.Application.Services.ReShadeWarmUpOutcome.NothingActive => "nothing-active",
        Stellar.Application.Services.ReShadeWarmUpOutcome.RenderDrewNothing => "render-drew-nothing",
        _ => "aborted",   // the capture threw before the plan ended
    };

    private static float ScreenAspect() => Screen.height > 0 ? (float)Screen.width / Screen.height : 0f;
}
