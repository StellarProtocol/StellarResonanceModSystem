using System;
using System.Collections;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Imaging;
using Stellar.Application.Services;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// The isolated capture (bridge 1.1.0, README § "Isolated capture"): the photo is drawn in a separate ReShade effect
/// runtime whose back buffer has the photo's size, so size-locked effects work at 2×/4× and nothing changes on screen.
/// An <see cref="IsolatedCapturePlanner"/> is advanced one step per frame at the end of the frame, with at most one
/// isolated render event per frame (the End step's freeing event aside). A session that began is ended on every exit
/// path — the plan's End step, this routine's finally (a throw), or host loss (<see cref="EndIsolatedIfOpen"/>). When
/// the planner reports Done(false) the box stays empty and the caller takes the planned fallback.
/// </summary>
internal sealed partial class UnityFrameGrabber
{
    private const int IsolatedRenderEventId = 1;   // the add-on ignores the id

    private readonly string? _isolatedConfigPath;   // absolute .ini the add-on may overwrite (+ "<name>.preset.ini")
    private bool _isolatedOpen;                     // begin was called and no end yet (main thread)

    // One isolated attempt: its plan, what to render, and the drawn photo held until the session is ended.
    private sealed class IsolatedSession
    {
        internal IsolatedSession(IsolatedCapturePlanner planner, GrabTarget target, CaptureFormat format, int quality) =>
            (Planner, Target, Format, Quality) = (planner, target, format, quality);
        internal IsolatedCapturePlanner Planner { get; }
        internal GrabTarget Target { get; }
        internal CaptureFormat Format { get; }
        internal int Quality { get; }
        internal FrameGrab? Drawn;
    }

    private IEnumerator IsolatedFrames(GrabTarget target, ReShadeCaptureOptions options, CaptureFormat format, int q, GrabBox box)
    {
        var supported = _reShade is { IsolatedSupported: true } && _isolatedConfigPath is not null
                        && _reShade.IsolatedEventFunc() != IntPtr.Zero;
        var planner = new IsolatedCapturePlanner(options.Active, options.Shaped, supported, Environment.TickCount64,
            warmUp: options.Isolated?.WarmUp ?? true);
        var started = Environment.TickCount64;
        var session = new IsolatedSession(planner, target, format, q);
        try
        {
            while (true)
            {
                if (!supported && planner.Next(0, Environment.TickCount64) is IsolatedStep.Done) yield break;   // never begun
                yield return new WaitForEndOfFrame();
                // Read one frame after the previous step's event, then act in this same end-of-frame.
                var step = planner.Next(supported ? _reShade!.IsolatedState() : 0, Environment.TickCount64);
                if (step is IsolatedStep.Done) yield break;
                RunIsolatedStep(step, session);
                if (step is IsolatedStep.End { Success: true }) box.Value = session.Drawn;
            }
        }
        finally
        {
            if (planner.NeedsEnd)
            {
                EndIsolatedIfOpen();
                planner.MarkEnded();
            }
            ReleaseIsolatedTargets();
            OnIsolatedCapture(planner, box.Value is not null, Environment.TickCount64 - started, target.Size);
        }
    }

    private void RunIsolatedStep(IsolatedStep step, IsolatedSession session)
    {
        var planner = session.Planner;
        switch (step)
        {
            case IsolatedStep.Begin begin:
                planner.AfterBegin(BeginIsolated(begin, session.Target.Size), Environment.TickCount64);
                IssueIsolatedEvent();
                break;
            case IsolatedStep.Pump:
                IssueIsolatedEvent();
                break;
            case IsolatedStep.WarmUpRender warm:
                IssueIsolatedRender(FillIsolatedWork(session.Target, warm.FreshCamera));   // nothing read back
                break;
            case IsolatedStep.Render render:
                var work = FillIsolatedWork(session.Target, render.FreshCamera);
                IssueIsolatedRender(work);
                var grab = ReadBack(work, session.Format, session.Quality);
                // The readback synced with the render thread, so the last render is this render's result.
                planner.AfterRender(_reShade!.IsolatedLastRender(), Environment.TickCount64);
                if (planner.Outcome != IsolatedOutcome.Drew) break;   // retried or failed: this frame is dropped
                RgbaAlpha.ForceOpaque(grab.RgbaBottomUp);   // ReShade leaves alpha 0; empty for JPG
                session.Drawn = grab with { Note = planner.SuccessNote };
                break;
            case IsolatedStep.End:
                EndIsolatedIfOpen();
                ReleaseIsolatedTargets();
                break;
        }
    }

    // Depth techniques are requested off first: requests made before begin decide what the isolated runtime compiles.
    private int BeginIsolated(IsolatedStep.Begin begin, CaptureSize size)
    {
        var bridge = _reShade!;
        foreach (var t in begin.DepthOff) bridge.IsolatedRequestTechnique(t.EffectFile, t.Name, on: false);
        _isolatedOpen = true;   // set before the call: whatever it returns, an end is owed
        return bridge.IsolatedBegin(size.Width, size.Height, _isolatedConfigPath!);
    }

    private void IssueIsolatedRender(RenderTexture rt)
    {
        _reShade!.IsolatedQueueRender(rt.GetNativeTexturePtr());
        IssueIsolatedEvent();
    }

    private void IssueIsolatedEvent()
    {
        var callback = _reShade?.IsolatedEventFunc() ?? IntPtr.Zero;
        if (callback != IntPtr.Zero) GL.IssuePluginEvent(callback, IsolatedRenderEventId);
    }

    /// <summary>Ends an open isolated session and issues the event that frees its video memory. Also called on host
    /// loss, when the coroutine that would have ended it is gone.</summary>
    private void EndIsolatedIfOpen()
    {
        if (!_isolatedOpen) return;
        _isolatedOpen = false;
        _reShade?.IsolatedEnd();
        IssueIsolatedEvent();
    }
}
