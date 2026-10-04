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
/// host GameObject) are only ever touched on the main thread. Each successful grab expects exactly one resume, and its
/// coroutine keeps PUMPING (draining <see cref="ResumeQueue"/>) only until that resume is served — no idle per-frame
/// work. A resume that arrives with no pump live is completed by <see cref="DrainQueuedResumes"/>, which the host calls
/// from its main-thread tick; a request is never completed on the requesting thread. Every returned task always
/// completes (faulted with <see cref="FrameGrabException"/> when the host is gone).</para>
/// </summary>
internal sealed partial class UnityFrameGrabber : IFrameGrabber
{
    private readonly IPluginLog _log;
    private readonly int _mainThreadId;
    private readonly object _gate = new();
    private readonly List<Action<Exception>> _pending = new();   // guarded by _gate
    private readonly ResumeQueue _resumes = new();
    private StellarCaptureHost? _host;                           // main thread only
    private bool _registered;

    /// <summary>Construct on the Unity main thread (the framework's Load()); its thread id is the main-thread id.
    /// <paramref name="reShade"/> is the bridge used when a grab asks for ReShade (null = never).
    /// <paramref name="isolatedConfigPath"/> is the absolute config file the bridge's isolated capture may write (with
    /// its preset copy beside it); null = never use the isolated capture.</summary>
    public UnityFrameGrabber(IPluginLog log, ReShadeBridge? reShade = null, string? isolatedConfigPath = null)
    {
        _log = log;
        _reShade = reShade;
        _isolatedConfigPath = isolatedConfigPath;
        _mainThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>Live resume pumps (0 when idle). Diagnostics / smoke evidence.</summary>
    internal int PumpCount => _resumes.Pumps;

    /// <summary>
    /// Main-thread tick hook: completes resumes that arrived while no pump was live. A single volatile read when idle.
    /// </summary>
    public void DrainQueuedResumes()
    {
        if (!_resumes.HasQueued || !OnMainThread) return;
        OnTickDrain(_resumes.Drain());
    }

    public (int Width, int Height) ScreenSize => (Screen.width, Screen.height);

    private int _maxTextureSize;   // main thread; read once (a GPU constant)
    public int MaxTextureSize => _maxTextureSize > 0 ? _maxTextureSize : _maxTextureSize = ReadMaxTextureSize();

    private bool OnMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    public Task<FrameGrab> GrabAsync(GrabTarget target, int settleFrames, CaptureFormat format, int jpgQuality)
    {
        var tcs = new TaskCompletionSource<FrameGrab>(); // no RunContinuationsAsynchronously: continuations stay on the main thread
        Run(ex => tcs.TrySetException(ex), GrabRoutine(tcs, target, settleFrames, format, jpgQuality));
        return tcs.Task;
    }

    public Task ResumeOnMainThreadAsync()
    {
        var tcs = new TaskCompletionSource<bool>(); // completed on the main thread (pump coroutine or host tick)
        if (OnMainThread && _resumes.Pumps == 0)
        {
            Run(ex => tcs.TrySetException(ex), NextFrame(tcs));
            return tcs.Task;
        }
        _resumes.Enqueue(tcs);   // a live pump serves it next frame; otherwise the host's main-thread tick drains it
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

    private IEnumerator GrabRoutine(TaskCompletionSource<FrameGrab> tcs, GrabTarget target, int settle, CaptureFormat format, int q)
    {
        for (var i = 0; i < settle; i++) yield return null;
        var box = new GrabBox();
        var frames = CaptureFrames(target, format, q, box);   // one end-of-frame without ReShade; one step per frame with it
        while (frames.MoveNext()) yield return frames.Current;
        var grab = box.Value ?? throw new FrameGrabException("The capture produced no frame.");   // a throw faults the grab (Tracked) and expects no resume
        _resumes.Expect();
        _resumes.PumpStarted();
        try
        {
            tcs.TrySetResult(grab);   // continuation runs inline; it resumes later from the pool thread
            // This coroutine keeps pumping until the resume: drop its hold on the frame (the local AND the
            // completed task's Result) so the pixel buffer is collectable as soon as the encode/write finishes.
            grab = null!;
            tcs = null!;
            var startedAt = Time.realtimeSinceStartup;
            while (_resumes.PumpShouldRun)
            {
                _resumes.Drain();
                if (!_resumes.PumpShouldRun) break;
                if (ResumeQueue.LeakGuardExpired(startedAt, Time.realtimeSinceStartup))
                {
                    _log.Error($"[PhotoStudio] capture resume never arrived after {ResumeQueue.LeakGuardSeconds:F0} s; pump stopped (leak guard).");
                    _resumes.Abandon();
                    break;
                }
                yield return null;
            }
        }
        finally
        {
            _resumes.PumpStopped();
            OnPumpStopped(_resumes.Pumps);
        }
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
        RestoreDepthOverrides();   // the coroutine that would have restored them is gone with the host
        ReleaseWarmUpTarget();     // ...and so is the one that would have released the scratch target
        EndIsolatedIfOpen();       // ...and the one that would have ended an isolated session
        ReleaseIsolatedTargets();  // ...and released its camera copies
        Action<Exception>[] pending;
        lock (_gate)
        {
            pending = _pending.ToArray();
            _pending.Clear();
        }
        foreach (var fail in pending) fail(new FrameGrabException("The capture was interrupted."));
        _resumes.FailAll(new FrameGrabException("The capture was interrupted."));
    }
}
