using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Unity;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// Grabs at end of frame via a coroutine and completes on the main thread (TCSes are created WITHOUT
/// RunContinuationsAsynchronously, so the awaiting continuation runs inline inside the coroutine).
/// Capture strategy = CameraRender (docs/recon/photo-studio-render-recon.md): the main camera renders once into an
/// N× RenderTexture and is read back as RGBA32 — clean (no game UI, no nameplates, no Stellar overlay) and a true
/// N× render. Every returned task always completes: a missing host, a failed StartCoroutine, a throwing capture or
/// the host's destruction all fault it with <see cref="FrameGrabException"/>.
/// </summary>
internal sealed class UnityFrameGrabber : IFrameGrabber
{
    private readonly IPluginLog _log;
    private readonly List<Action<Exception>> _pending = new();
    private StellarCaptureHost? _host;
    private bool _registered;

    public UnityFrameGrabber(IPluginLog log) => _log = log;

    public (int Width, int Height) ScreenSize => (Screen.width, Screen.height);

    public Task<FrameGrab> GrabAsync(int scale, int settleFrames, CaptureFormat format, int jpgQuality)
    {
        var tcs = new TaskCompletionSource<FrameGrab>(); // no RunContinuationsAsynchronously: continuations stay on the main thread
        Run(ex => tcs.TrySetException(ex), GrabRoutine(tcs, scale, settleFrames, format, jpgQuality));
        return tcs.Task;
    }

    public Task ResumeOnMainThreadAsync()
    {
        var tcs = new TaskCompletionSource<bool>(); // completes inside the coroutine = main thread
        Run(ex => tcs.TrySetException(ex), NextFrame(tcs));
        return tcs.Task;
    }

    private void Run(Action<Exception> fail, IEnumerator body)
    {
        try
        {
            var host = EnsureHost();
            _pending.Add(fail);
            host.StartCoroutine(Tracked(body, fail).WrapToIl2Cpp());
        }
        catch (Exception ex)
        {
            _pending.Remove(fail);
            fail(ex as FrameGrabException ?? new FrameGrabException(ex.Message));
        }
    }

    // Flattens the managed body (only null / WaitForEndOfFrame cross into Unity) and turns a throw into a fault.
    private IEnumerator Tracked(IEnumerator inner, Action<Exception> fail)
    {
        while (true)
        {
            object? current;
            try
            {
                if (!inner.MoveNext()) break;
                current = inner.Current;
            }
            catch (Exception ex)
            {
                fail(ex as FrameGrabException ?? new FrameGrabException(ex.Message));
                break;
            }
            yield return current;
        }
        _pending.Remove(fail);
    }

    private static IEnumerator NextFrame(TaskCompletionSource<bool> tcs)
    {
        yield return null;
        tcs.TrySetResult(true);
    }

    private static IEnumerator GrabRoutine(TaskCompletionSource<FrameGrab> tcs, int scale, int settle, CaptureFormat format, int q)
    {
        for (var i = 0; i < settle; i++) yield return null;
        yield return new WaitForEndOfFrame();
        tcs.TrySetResult(Capture(scale, format, q));
    }

    private StellarCaptureHost EnsureHost()
    {
        if (_host != null) return _host;
        if (!_registered)
        {
            Il2CppClassInitFix.EnsureSeeded(_log);
            try { ClassInjector.RegisterTypeInIl2Cpp<StellarCaptureHost>(); }
            catch (Exception ex) { _log.Debug($"[PhotoStudio] RegisterTypeInIl2Cpp(StellarCaptureHost): {ex.Message}"); }
            _registered = true;
        }
        var go = new GameObject("StellarCaptureHost") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(go);
        var host = go.AddComponent<StellarCaptureHost>() ?? throw new FrameGrabException("The capture host could not be created.");
        host.Destroyed = FailPending;
        _host = host;
        return host;
    }

    private void FailPending()
    {
        _host = null;
        var pending = _pending.ToArray();
        _pending.Clear();
        foreach (var fail in pending) fail(new FrameGrabException("The capture was interrupted."));
    }

    private static FrameGrab Capture(int scale, CaptureFormat format, int q)
    {
        var cam = Camera.main;
        if (cam == null) throw new FrameGrabException("No camera is rendering the scene.");
        int w = Screen.width * scale, h = Screen.height * scale;
        var rt = new RenderTexture(w, h, 24);
        var prevTarget = cam.targetTexture;
        var prevActive = RenderTexture.active;
        Texture2D? tex = null;
        try
        {
            if (!rt.Create()) throw new FrameGrabException($"A {w}x{h} render target could not be created.");
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prevTarget;
            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
            return Encode(tex, w, h, format, q);
        }
        finally
        {
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            rt.Release();
            UnityEngine.Object.Destroy(rt);
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }
    }

    private static FrameGrab Encode(Texture2D tex, int w, int h, CaptureFormat format, int q)
    {
        byte[]? jpeg = format == CaptureFormat.Jpg ? Copy(ImageConversion.EncodeToJPG(tex, q)) : null;
        var raw = jpeg is null ? Copy(tex.GetRawTextureData()) : Array.Empty<byte>();
        if (jpeg is null && raw.Length != w * h * 4) throw new FrameGrabException("The captured frame has an unexpected pixel format.");
        return new FrameGrab(raw, w, h, jpeg);
    }

    // One memcpy over the native buffer (AsSpan) — never enumerate an Il2Cpp array element-by-element (132 MB at 4×).
    private static byte[] Copy(Il2CppStructArray<byte>? src)
    {
        if (src is null) throw new FrameGrabException("The frame could not be read back.");
        return src.AsSpan().ToArray();
    }
}
