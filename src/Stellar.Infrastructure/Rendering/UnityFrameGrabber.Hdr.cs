using System;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// HDR camera render for the capture (KB Photo-Capture-HDR-Bloom.md). The game's pipeline (URP 10 fork) takes the colour
/// format from the camera's <c>targetTexture</c>, so rendering straight into the 8-bit photo target clamps every value
/// above 1.0 BEFORE the bloom threshold — the rim-light glow is lost. The camera therefore renders into a transient
/// float target (allowHDR forced for that render only), which is then resolved with one <c>Graphics.Blit</c> into the
/// unchanged LDR photo target. Everything downstream — ReShade's queued texture, the isolated work/pristine copies
/// (<c>CopyTexture</c> needs identical descriptors), the warm-up target, <c>ReadPixels</c> into RGBA32 — keeps seeing
/// exactly the target it saw before, so only the camera render changes.
/// <para>Colour: the photo target is the old default (<c>ARGB32</c>, <c>RenderTextureReadWrite.Default</c> = sRGB in a
/// Linear-space project). A float target has no sRGB variant — it stores the pipeline's linear output as-is. The blit
/// samples those linear values and the sRGB photo target encodes them on write, exactly as the pipeline's own final
/// write into that target did before; in a Gamma-space project both are plain copies. So the photo has the old colours
/// with no gamma shift. Reading the float target directly into the sRGB RGBA32 texture was avoided on purpose: whether
/// <c>ReadPixels</c> gamma-encodes on a float→8-bit conversion is driver/Unity-version dependent.</para>
/// <para>A/B switch: <c>STELLAR_CAPTURE_LDR=1</c> renders straight into the photo target as before (read once).</para>
/// </summary>
internal sealed partial class UnityFrameGrabber
{
    private static readonly bool ForceLdrCapture = Environment.GetEnvironmentVariable("STELLAR_CAPTURE_LDR") == "1";

    private RenderTextureFormat? _hdrFormat;   // decided at the first capture; null = not decided yet
    private bool _hdrUnavailable;              // no float format, or a float target failed to create: LDR from then on
    private bool _hdrFormatLogged;
    // Reused across one capture's camera renders (ReShade warm-up frames render every frame): a 4x float target is
    // about twice the ~260 MB 4x photo target, so it is never allocated per frame. Released with the photo targets (ReleaseHdrScratch).
    private RenderTexture? _hdrScratch;

    /// <summary>Renders the main camera into a transient HDR target and resolves it into <paramref name="rt"/>; renders
    /// straight into <paramref name="rt"/> when HDR is off (A/B switch) or unavailable.</summary>
    private void RenderCameraHdr(Camera cam, RenderTexture rt)
    {
        var hdr = HdrScratch(rt.width, rt.height);
        if (hdr == null)
        {
            cam.targetTexture = rt;
            cam.Render();
            return;
        }
        var prevHdr = cam.allowHDR;
        var prevActive = RenderTexture.active;
        try
        {
            cam.allowHDR = true;
            cam.targetTexture = hdr;
            cam.Render();
            Graphics.Blit(hdr, rt);   // float → sRGB 8-bit: clamps and gamma-encodes like the pipeline's old final write
        }
        finally
        {
            try { cam.allowHDR = prevHdr; }                     // RenderCamera's finally restores targetTexture
            finally { RenderTexture.active = prevActive; }     // Blit leaves the destination active
        }
    }

    private RenderTexture? HdrScratch(int w, int h)
    {
        if (_hdrScratch != null && _hdrScratch.width == w && _hdrScratch.height == h) return _hdrScratch;
        ReleaseHdrScratch();
        return _hdrScratch = NewHdrTarget(w, h);
    }

    /// <summary>Frees the scratch float target. Called wherever a capture's photo targets are released (end of a
    /// capture, warm-up/isolated release, host loss).</summary>
    private void ReleaseHdrScratch()
    {
        DestroyTarget(_hdrScratch);
        _hdrScratch = null;
    }

    /// <summary>A created float render target (+ 24-bit depth for the camera), or null for the LDR path. Logs the
    /// chosen format once.</summary>
    private RenderTexture? NewHdrTarget(int w, int h)
    {
        if (ForceLdrCapture || _hdrUnavailable)
        {
            LogCaptureFormat(null);
            return null;
        }
        _hdrFormat ??= PickHdrFormat();
        if (_hdrFormat is not { } format)
        {
            _hdrUnavailable = true;
            LogCaptureFormat(null);
            return null;
        }
        // Linear: a float format has no sRGB variant; stating it keeps Unity from guessing per colour space.
        var rt = new RenderTexture(w, h, 24, format, RenderTextureReadWrite.Linear);
        if (rt.Create())
        {
            LogCaptureFormat(format);
            return rt;
        }
        UnityEngine.Object.Destroy(rt);
        _hdrUnavailable = true;
        _log.Warning($"[PhotoStudio] a {w}x{h} {format} capture target could not be created; capturing LDR (no bloom) from now on.");
        return null;
    }

    private static RenderTextureFormat? PickHdrFormat()
    {
        try
        {
            if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.DefaultHDR)) return RenderTextureFormat.DefaultHDR;
            if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)) return RenderTextureFormat.ARGBHalf;
        }
        catch (Exception) { }   // treated as unsupported: the LDR path still captures
        return null;
    }

    private void LogCaptureFormat(RenderTextureFormat? hdr)
    {
        if (_hdrFormatLogged) return;
        _hdrFormatLogged = true;
        string space;
        try { space = QualitySettings.activeColorSpace.ToString(); }
        catch (Exception) { space = "?"; }
        _log.Info(hdr is { } f
            ? $"[PhotoStudio] capture target format: {f} (allowHDR forced, resolved into the LDR photo target; colour space {space})"
            : $"[PhotoStudio] capture target format: LDR default ({(ForceLdrCapture ? "STELLAR_CAPTURE_LDR=1" : "no HDR format")}; colour space {space})");
    }
}
