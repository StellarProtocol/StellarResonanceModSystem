using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// One step of an <see cref="IsolatedCapturePlanner"/> session. <see cref="Done"/> is absorbing.
/// </summary>
internal abstract record IsolatedStep
{
    /// <summary>Request <see cref="DepthOff"/> off in the isolated runtime, call begin, report it with
    /// <see cref="IsolatedCapturePlanner.AfterBegin"/>, and issue the isolated render event once.</summary>
    internal sealed record Begin(IReadOnlyList<ReShadeTechniqueRef> DepthOff) : IsolatedStep;
    /// <summary>Issue the isolated render event once (the runtime is starting or compiling).</summary>
    internal sealed record Pump : IsolatedStep;
    /// <summary>Render the camera into the photo's texture, queue it, issue the event in this frame, read it back and
    /// report the bridge's last render with <see cref="IsolatedCapturePlanner.AfterRender"/>.</summary>
    internal sealed record Render : IsolatedStep;
    /// <summary>End the session and issue the event once more (frees the video memory). <see cref="Success"/>: keep the
    /// rendered photo; otherwise take the fallback.</summary>
    internal sealed record End(bool Success) : IsolatedStep;
    /// <summary>Finished: <see cref="Success"/> = the photo was drawn isolated; false = take the fallback.</summary>
    internal sealed record Done(bool Success) : IsolatedStep;
}

/// <summary>How an isolated session ended (diagnostics and tests).</summary>
internal enum IsolatedOutcome
{
    /// <summary>Still running.</summary>
    Pending,
    /// <summary>The photo was drawn.</summary>
    Drew,
    /// <summary>The bridge has no isolated capture (1.0.0): never begun.</summary>
    Unsupported,
    /// <summary>Nothing to draw: every active technique uses depth (never begun), or the runtime reported state 4.</summary>
    NothingToDraw,
    /// <summary>Begin was refused, the state went negative, or the render reported an error.</summary>
    Error,
    /// <summary>Not drawn within <see cref="IsolatedCapturePlanner.TimeoutMs"/>.</summary>
    TimedOut,
}

/// <summary>
/// Pure per-photo state machine for the bridge's isolated capture (StellarReShadeBridge 1.1.0 README § "Isolated
/// capture"): begin → pump the render event while the runtime starts (state 1) and compiles (state 2) → render at state 3
/// → end. One call per frame; no I/O. Any failure ends the session (a session that began is ALWAYS ended — the bridge
/// keeps an error state and its video memory until then) and reports Done(false) so the grabber takes the fallback.
/// Depth-using techniques are requested off: the isolated runtime has no game depth buffer.
/// </summary>
internal sealed class IsolatedCapturePlanner
{
    /// <summary>The whole session's bound — generous, because the first compile at a new size can take seconds.</summary>
    internal const long TimeoutMs = 20000;

    // rsb_isolated_state / rsb_isolated_begin / rsb_isolated_last_render codes used here.
    private const int StateReady = 3, StateNothingToDraw = 4, BeginQueued = 1, RenderNotReady = -4;

    private enum Phase { Begin, AwaitBegin, Pumping, AwaitRender, Ending, Done }

    private readonly IReadOnlyList<ReShadeTechniqueRef> _depthOff;
    private readonly long _startMs;
    private Phase _phase;
    private bool _success;
    private bool _begun;   // begin may have been called: an end is owed until MarkEnded / the End step

    /// <summary><paramref name="active"/>: the enabled techniques. <paramref name="supported"/>: the bridge exports
    /// the isolated functions.</summary>
    internal IsolatedCapturePlanner(IReadOnlyList<ReShadeTechnique> active, bool shaped, bool supported, long nowMs)
    {
        _startMs = nowMs;
        _depthOff = CollectDepth(active);
        SuccessNote = !shaped && _depthOff.Count > 0 ? ReShadeCaptureNotes.DepthLeftOut : null;
        if (!supported) Finish(IsolatedOutcome.Unsupported);
        else if (_depthOff.Count == active.Count) Finish(IsolatedOutcome.NothingToDraw);
    }

    /// <summary>Why the session ended (<see cref="IsolatedOutcome.Pending"/> while running).</summary>
    internal IsolatedOutcome Outcome { get; private set; }

    /// <summary>The last state (or refused begin code) seen — diagnostics.</summary>
    internal int FinalState { get; private set; }

    /// <summary>The last render result reported (0 when none) — diagnostics.</summary>
    internal int RenderCode { get; private set; }

    /// <summary>Frames spent waiting for the runtime (Pump steps).</summary>
    internal int FramesWaited { get; private set; }

    /// <summary>The note a successful isolated photo carries, or null: a window-shaped photo whose depth techniques
    /// were left out says so (a shaped photo always leaves them out, as documented).</summary>
    internal string? SuccessNote { get; }

    /// <summary>True while a session may be open and no End step has been returned — the grabber ends it itself when
    /// the capture throws.</summary>
    internal bool NeedsEnd => _begun;

    /// <summary>The grabber ended the session outside the plan (on a throw).</summary>
    internal void MarkEnded() => _begun = false;

    /// <summary>Advances one frame. <paramref name="state"/> is <c>rsb_isolated_state()</c> read after the previous
    /// frame's event.</summary>
    internal IsolatedStep Next(int state, long nowMs)
    {
        switch (_phase)
        {
            case Phase.Begin:
                _phase = Phase.AwaitBegin;
                _begun = true;
                return new IsolatedStep.Begin(_depthOff);
            case Phase.Pumping:
                return Advance(state, nowMs);
            case Phase.Ending:
                _phase = Phase.Done;
                _begun = false;
                return new IsolatedStep.End(_success);
            case Phase.Done:
                return new IsolatedStep.Done(_success);
            default:   // AwaitBegin / AwaitRender without the report: treat as a broken contract and stop
                Fail(IsolatedOutcome.Error);
                return Next(state, nowMs);
        }
    }

    /// <summary>Reports <c>rsb_isolated_begin</c>'s return code (1 = queued).</summary>
    internal void AfterBegin(int code, long nowMs)
    {
        if (_phase != Phase.AwaitBegin) return;
        _phase = Phase.Pumping;
        if (code == BeginQueued) return;
        FinalState = code;
        Fail(IsolatedOutcome.Error);
    }

    /// <summary>Reports <c>rsb_isolated_last_render()</c> after the readback: drawn count, or an error.</summary>
    internal void AfterRender(int code, long nowMs)
    {
        if (_phase != Phase.AwaitRender) return;
        RenderCode = code;
        if (code > 0)
        {
            _success = true;
            Outcome = IsolatedOutcome.Drew;
            _phase = Phase.Ending;
        }
        else if (code is 0 or RenderNotReady)
        {
            _phase = Phase.Pumping;   // loading again / nothing yet: pump and render again, within the bound
            if (PastTimeout(nowMs)) Fail(IsolatedOutcome.TimedOut);
        }
        else
        {
            Fail(IsolatedOutcome.Error);
        }
    }

    private IsolatedStep Advance(int state, long nowMs)
    {
        FinalState = state;
        if (state == StateReady && !PastTimeout(nowMs))
        {
            _phase = Phase.AwaitRender;
            return new IsolatedStep.Render();
        }
        if (state < 0) Fail(IsolatedOutcome.Error);
        else if (state == StateNothingToDraw) Fail(IsolatedOutcome.NothingToDraw);
        else if (PastTimeout(nowMs)) Fail(IsolatedOutcome.TimedOut);
        else
        {
            FramesWaited++;   // 1 starting, 2 compiling (0 / 5 only transiently around a begin)
            return new IsolatedStep.Pump();
        }
        return Next(state, nowMs);
    }

    private bool PastTimeout(long nowMs) => nowMs - _startMs >= TimeoutMs;

    // A begun session ends first (Ending → End step → Done); one never begun is done at once.
    private void Fail(IsolatedOutcome outcome)
    {
        Outcome = outcome;
        _success = false;
        _phase = _begun ? Phase.Ending : Phase.Done;
    }

    private void Finish(IsolatedOutcome outcome)
    {
        Outcome = outcome;
        _phase = Phase.Done;
    }

    private static IReadOnlyList<ReShadeTechniqueRef> CollectDepth(IReadOnlyList<ReShadeTechnique> active)
    {
        List<ReShadeTechniqueRef>? names = null;
        foreach (var technique in active)
        {
            if (!technique.UsesDepth) continue;
            (names ??= new List<ReShadeTechniqueRef>()).Add(new ReShadeTechniqueRef(technique.EffectFile, technique.Name));
        }
        return (IReadOnlyList<ReShadeTechniqueRef>?)names ?? Array.Empty<ReShadeTechniqueRef>();
    }
}
