using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.Injection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Unity;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

// Frame capture + readback lives in UnityFrameGrabber.Capture.cs; diagnostics in UnityFrameGrabber.Diagnostics.cs.

/// <summary>
/// Grabs at end of frame via a coroutine and completes on the main thread (TCSes are created WITHOUT
/// RunContinuationsAsynchronously, so the awaiting continuation runs inline inside the coroutine).
/// Capture strategy = CameraRender (docs/recon/photo-studio-render-recon.md).
///
/// <para>Threading: <see cref="GrabAsync"/> is called on the main thread; <see cref="ResumeOnMainThreadAsync"/> is
/// usually called from a thread-pool thread (after the off-thread encode/write). Unity APIs (StartCoroutine, the
/// host GameObject) are only ever touched on the main thread: after a grab its coroutine keeps PUMPING for
/// <see cref="PumpIdleSeconds"/>, draining resume requests queued from other threads. An off-thread resume with no
/// pump alive fails fast rather than touching Unity. Every returned task always completes (faulted with
/// <see cref="FrameGrabException"/> when the host is gone).</para>
/// </summary>
internal sealed partial class UnityFrameGrabber : IFrameGrabber
{
    private const float PumpIdleSeconds = 60f;   // covers a 4× PNG encode + write (measured ~2 s) with a wide margin

    private readonly IPluginLog _log;
    private readonly int _mainThreadId;
    private readonly object _gate = new();
    private readonly List<Action<Exception>> _pending = new();                 // guarded by _gate
    private readonly List<TaskCompletionSource<bool>> _resumes = new();       // guarded by _gate
    private int _pumps;                                                        // guarded by _gate
    private StellarCaptureHost? _host;                                         // main thread only
    private bool _registered;

    /// <summary>Construct on the Unity main thread (the framework's Load()); its thread id is the main-thread id.</summary>
    public UnityFrameGrabber(IPluginLog log)
    {
        _log = log;
        _mainThreadId = Environment.CurrentManagedThreadId;
    }

    public (int Width, int Height) ScreenSize => (Screen.width, Screen.height);

    private bool OnMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public Task<FrameGrab> GrabAsync(int scale, int settleFrames, CaptureFormat format, int jpgQuality)
    {
        var tcs = new TaskCompletionSource<FrameGrab>(); // no RunContinuationsAsynchronously: continuations stay on the main thread
        Run(ex => tcs.TrySetException(ex), GrabRoutine(tcs, scale, settleFrames, format, jpgQuality));
        return tcs.Task;
    }

    public Task ResumeOnMainThreadAsync()
    {
        var tcs = new TaskCompletionSource<bool>(); // completed on the main thread (coroutine or pump)
        if (OnMainThread)
        {
            Run(ex => tcs.TrySetException(ex), NextFrame(tcs));
            return tcs.Task;
        }
        lock (_gate)
        {
            if (_pumps > 0) { _resumes.Add(tcs); return tcs.Task; }
        }
        tcs.TrySetException(new FrameGrabException("The capture host is not running on the main thread."));
        return tcs.Task;
    }

    private void Run(Action<Exception> fail, IEnumerator body)
    {
        try
        {
            if (!OnMainThread) throw new FrameGrabException("Captures must be started on the main thread.");
            var host = EnsureHost();
            lock (_gate) _pending.Add(fail);
            host.StartCoroutine(Tracked(body, fail).WrapToIl2Cpp());
        }
        catch (Exception ex)
        {
            lock (_gate) _pending.Remove(fail);
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
        lock (_gate) _pending.Remove(fail);
    }

    private static IEnumerator NextFrame(TaskCompletionSource<bool> tcs)
    {
        yield return null;
        tcs.TrySetResult(true);
    }

    private IEnumerator GrabRoutine(TaskCompletionSource<FrameGrab> tcs, int scale, int settle, CaptureFormat format, int q)
    {
        lock (_gate) _pumps++;
        try
        {
            for (var i = 0; i < settle; i++) yield return null;
            yield return new WaitForEndOfFrame();
            tcs.TrySetResult(Capture(scale, format, q));
            var idleUntil = Time.realtimeSinceStartup + PumpIdleSeconds;
            while (Time.realtimeSinceStartup < idleUntil)
            {
                DrainResumes();
                yield return null;
            }
        }
        finally
        {
            lock (_gate) _pumps = Math.Max(0, _pumps - 1);
            DrainResumes();   // a resume queued between the last drain and the decrement still completes
        }
    }

    // Completes queued off-thread resumes on the main thread (outside the lock: continuations run inline).
    private void DrainResumes()
    {
        TaskCompletionSource<bool>[] ready;
        lock (_gate)
        {
            if (_resumes.Count == 0) return;
            ready = _resumes.ToArray();
            _resumes.Clear();
        }
        foreach (var r in ready) r.TrySetResult(true);
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
        Action<Exception>[] pending;
        TaskCompletionSource<bool>[] resumes;
        lock (_gate)
        {
            pending = _pending.ToArray();
            resumes = _resumes.ToArray();
            _pending.Clear();
            _resumes.Clear();
            _pumps = 0;
        }
        foreach (var fail in pending) fail(new FrameGrabException("The capture was interrupted."));
        foreach (var r in resumes) r.TrySetException(new FrameGrabException("The capture was interrupted."));
    }
}
