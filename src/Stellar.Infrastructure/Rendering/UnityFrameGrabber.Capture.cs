using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary>CameraRender capture: main camera → target-sized RenderTexture → ReadPixels (RGBA32, rows bottom-up). A
/// shaped target renders with the camera's lens (aspect, view angle; gate fit + focal length on a physical camera) set
/// for that render only — <see cref="CaptureLensPlanner"/>.</summary>
internal sealed partial class UnityFrameGrabber
{
    private bool _nativeReadbackFailed;

    /// <param name="applyReShade">Have ReShade draw its effects into the render target between the camera render and
    /// the readback (UnityFrameGrabber.ReShade.cs). The caller checks the result and fixes alpha.</param>
    private FrameGrab Capture(GrabTarget target, CaptureFormat format, int q, bool applyReShade = false)
    {
        var cam = Camera.main;
        if (cam == null) throw new FrameGrabException("No camera is rendering the scene.");
        int w = target.Size.Width, h = target.Size.Height;
        if (w <= 0 || h <= 0) throw new FrameGrabException("The photo size is empty.");
        var rt = new RenderTexture(w, h, 24);
        var prevTarget = cam.targetTexture;
        var prevActive = RenderTexture.active;
        Texture2D? tex = null;
        CaptureLensOverride? lens = null, restored = null;
        try
        {
            if (!rt.Create()) throw new FrameGrabException($"A {w}x{h} render target could not be created.");
            lens = CaptureLensOverride.Apply(new UnityCaptureLens(cam), target, Screen.width, Screen.height);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevTarget;
            RestoreLens(ref lens, ref restored);
            if (applyReShade) IssueReShadeRender(rt, w, h);   // render thread, before the readback below syncs with it
            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            // ReadPixels fills the texture's CPU copy, which is all the readback/JPG encode read — no Apply()
            // (that would upload the whole frame back to the GPU for nothing).
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            return Encode(tex, w, h, format, q);
        }
        finally
        {
            try { RestoreLens(ref lens, ref restored); }   // the camera's lens is back before anything else renders
            finally
            {
                // A failed lens restore must not skip the render-target cleanup.
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                rt.Release();
                UnityEngine.Object.Destroy(rt);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
            if (restored is not null) OnLensRestored(cam, restored);   // diagnostics last, after every cleanup; never throws
        }
    }

    private static void RestoreLens(ref CaptureLensOverride? lens, ref CaptureLensOverride? restored)
    {
        if (lens is null) return;
        restored = lens;
        lens = null;            // cleared first: a restore runs every step exactly once, even when one of them throws
        restored.Dispose();
    }

    private static int ReadMaxTextureSize()
    {
        try { return SystemInfo.maxTextureSize; }
        catch (Exception) { return CaptureSizing.MaxLongSide; }   // the long-side cap still holds
    }

    // JPG stays on the main thread: EncodeToJPG reads the texture in place, while the thread-safe
    // EncodeArrayToJPG would need a second full-frame IL2CPP array AND an il2cpp_thread_attach'ed pool thread —
    // neither BepInEx nor Il2CppInterop attaches managed threads (see IScreenCapture remarks for the evidence).
    private FrameGrab Encode(Texture2D tex, int w, int h, CaptureFormat format, int q)
    {
        byte[]? jpeg = format == CaptureFormat.Jpg ? CopyIl2Cpp(ImageConversion.EncodeToJPG(tex, q)) : null;
        var raw = jpeg is null ? ReadRaw(tex, w * h * 4) : Array.Empty<byte>();
        if (jpeg is null && raw.Length != w * h * 4) throw new FrameGrabException("The captured frame has an unexpected pixel format.");
        return new FrameGrab(raw, w, h, jpeg);
    }

    /// <summary>
    /// Copies the texture's CPU pixels straight out of Unity's own buffer (<c>GetRawTextureData&lt;byte&gt;()</c> is a
    /// NativeArray VIEW — no allocation), so a 4× grab holds one managed copy instead of an extra 132 MB IL2CPP array.
    /// Falls back to the IL2CPP-array overload if the generic instantiation is unavailable on this build.
    /// </summary>
    private byte[] ReadRaw(Texture2D tex, int expected)
    {
        if (!_nativeReadbackFailed)
        {
            try
            {
                var view = tex.GetRawTextureData<byte>();
                var dst = new byte[view.Length];
                unsafe { Marshal.Copy((IntPtr)view.m_Buffer, dst, 0, dst.Length); }
                OnReadback("NativeArray", dst.Length, expected);
                return dst;
            }
            catch (Exception ex)
            {
                _nativeReadbackFailed = true;
                _log.Warning($"[PhotoStudio] native readback unavailable ({ex.GetType().Name}: {ex.Message}); using the array copy.");
            }
        }
        var copy = CopyIl2Cpp(tex.GetRawTextureData());
        OnReadback("Il2CppArray", copy.Length, expected);
        return copy;
    }

    // One memcpy over the native buffer (AsSpan) — never enumerate an Il2Cpp array element-by-element.
    private static byte[] CopyIl2Cpp(Il2CppStructArray<byte>? src)
    {
        if (src is null) throw new FrameGrabException("The frame could not be read back.");
        return src.AsSpan().ToArray();
    }
}
