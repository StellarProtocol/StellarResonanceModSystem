using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>A technique's identity: its effect file (as ReShade reports it) and its name — names repeat across effects,
/// so a toggle by name alone would hit every same-named technique in every pack.</summary>
internal sealed record ReShadeTechniqueRef(string EffectFile, string Name);

/// <summary>
/// One step of a <see cref="ReShadeCapturePlanner"/> plan. <see cref="Done"/> is absorbing — once returned, the
/// planner returns the same <see cref="Done"/> forever.
/// </summary>
internal abstract record CaptureStep
{
    /// <summary>Let ReShade draw one more frame; keep calling until the queued render reports it drew something.</summary>
    internal sealed record WarmUp : CaptureStep;
    /// <summary>ReShade is (re)loading effects: draw nothing this frame (a render now would return early, drawing 0).</summary>
    internal sealed record Wait : CaptureStep;
    /// <summary>Turn off these techniques for this capture — the depth-using ones of a shaped capture and, when asked,
    /// the size-locked ones (once; never saved).</summary>
    internal sealed record DisableDepth(IReadOnlyList<ReShadeTechniqueRef> Techniques) : CaptureStep;
    /// <summary>Queue the actual capture render, then report its result with
    /// <see cref="ReShadeCapturePlanner.AfterRender"/>. Returned once per attempt (again only after a
    /// <see cref="RenderVerdict.Retry"/>).</summary>
    internal sealed record Render : CaptureStep;
    /// <summary>Turn these techniques back on (mirrors an earlier <see cref="DisableDepth"/>; never saved).</summary>
    internal sealed record Restore(IReadOnlyList<ReShadeTechniqueRef> Techniques) : CaptureStep;
    /// <summary>The plan is finished. <see cref="Applied"/> is false when nothing was drawn (nothing to draw, the
    /// warm-up timed out, or a hard error).</summary>
    internal sealed record Done(bool Applied) : CaptureStep;
}

/// <summary>What to do with the photo the real render just produced.</summary>
internal enum RenderVerdict
{
    /// <summary>ReShade drew: keep it.</summary>
    Keep,
    /// <summary>ReShade drew nothing but there is time left: discard it and warm up again.</summary>
    Retry,
    /// <summary>Give up: keep the photo, which has no ReShade in it (see <see cref="ReShadeCapturePlanner.Outcome"/>).</summary>
    KeepWithoutReShade,
}

/// <summary>Why a <see cref="ReShadeCapturePlanner"/> plan ended (notes, diagnostics and tests).</summary>
internal enum ReShadeWarmUpOutcome
{
    /// <summary>Still running.</summary>
    Pending,
    /// <summary>ReShade drew (a warm-up, and the real render when it was reported).</summary>
    Drew,
    /// <summary>The warm-up drew nothing before its deadline (<see cref="ReShadeCapturePlanner.WarmUpTimeoutMs"/>).</summary>
    TimedOut,
    /// <summary>The bridge reported a hard error (<see cref="ReShadeCapturePlanner.IsHardError"/>); no point waiting.</summary>
    Error,
    /// <summary>No technique left to draw.</summary>
    NothingActive,
    /// <summary>The real render kept drawing nothing until the deadline.</summary>
    RenderDrewNothing,
}

/// <summary>
/// Pure per-capture state machine deciding what the ReShade capture pass should do next, one call per frame.
/// Construct one instance per capture; it holds no reference to anything renderable. No I/O, no threading.
/// <para>A warm-up's drawn count only counts on the frame right after that warm-up, and only while ReShade is not
/// loading: when ReShade first draws at a new size it applies the preset again, which can switch on further techniques
/// (AcerolaFX's hidden setup passes) whose compile then starts — a render during that load draws 0 (ReShade 6.8.0
/// runtime.cpp:3722, 3810). So the plan waits out a load and warms up again before the real render, and a real render
/// that still drew nothing goes back to warm-up instead of ending in a photo without ReShade.</para>
/// <para>Deadline: <see cref="WarmUpTimeoutMs"/> of time NOT spent loading, and never more than <see cref="MaxWaitMs"/>
/// in all.</para>
/// </summary>
internal sealed class ReShadeCapturePlanner
{
    /// <summary>How long a plan may try (not counting time ReShade spends loading) before it gives up.</summary>
    internal const long WarmUpTimeoutMs = 5000;

    /// <summary>The hard cap on a plan, loading included.</summary>
    internal const long MaxWaitMs = 20000;

    /// <summary>The bridge's render result codes that cannot recover by waiting: -1 no ReShade runtime (or no add-on),
    /// -3 the render-target view could not be created. (-2 "nothing queued" and 0 "nothing drawn yet" keep warming.)</summary>
    internal static bool IsHardError(int lastRender) => lastRender is -1 or -3;

    /// <summary>Whether a capture switches <paramref name="technique"/> off: depth in a shaped capture, and a size-locked
    /// effect when <paramref name="skipSizeLocked"/>.</summary>
    internal static bool Skips(ReShadeTechnique technique, bool shaped, bool skipSizeLocked) =>
        (shaped && technique.UsesDepth) || (skipSizeLocked && technique.SizeLocked);

    /// <summary>Why the plan ended (<see cref="ReShadeWarmUpOutcome.Pending"/> while running).</summary>
    internal ReShadeWarmUpOutcome Outcome { get; private set; }

    /// <summary>The <c>lastDrawn</c> value the warm-up ended on (the drawn count, or the error code).</summary>
    internal int WarmUpEndCode { get; private set; }

    /// <summary>Real renders that drew nothing and were retried (diagnostics).</summary>
    internal int Retries { get; private set; }

    private enum Phase
    {
        DisableDepth,
        WarmUp,
        Restore,
        DoneSuccess,
        DoneFail,
    }

    private readonly IReadOnlyList<ReShadeTechniqueRef> _skipped;
    private Phase _phase;
    private long _warmUpStartMs;
    private long _lastNowMs;
    private long _loadingMs;
    private bool _warmUpQueued;     // the previous step was a WarmUp: its drawn count is the one being reported
    private bool _awaitingRender;   // a Render was returned and its result is not reported yet

    /// <summary>
    /// <paramref name="active"/> must already be the active techniques (<c>Enabled == true</c>). The ones
    /// <see cref="Skips"/> picks are disabled first and restored after the render; when that is every one of them the
    /// plan ends at once (<see cref="ReShadeWarmUpOutcome.NothingActive"/>) instead of warming up for nothing.
    /// </summary>
    internal ReShadeCapturePlanner(bool shaped, IReadOnlyList<ReShadeTechnique> active, long nowMs, bool skipSizeLocked = false)
    {
        _skipped = CollectSkipped(active, shaped, skipSizeLocked);
        if (_skipped.Count == active.Count)
        {
            _skipped = Array.Empty<ReShadeTechniqueRef>();
            _phase = Phase.DoneFail;
            Outcome = ReShadeWarmUpOutcome.NothingActive;
            return;
        }
        if (_skipped.Count > 0) _phase = Phase.DisableDepth;
        else StartWarmUp(nowMs);
    }

    /// <summary><see cref="Next(int, bool, long)"/> with ReShade not loading.</summary>
    internal CaptureStep Next(int lastDrawn, long nowMs) => Next(lastDrawn, loading: false, nowMs);

    /// <summary>Advances the plan by one frame. <paramref name="lastDrawn"/> is the bridge's last render result (how
    /// many techniques the previous frame's queued render drew, or an error code); <paramref name="loading"/> is whether
    /// ReShade is loading effects right now.</summary>
    internal CaptureStep Next(int lastDrawn, bool loading, long nowMs)
    {
        var drawn = _warmUpQueued ? lastDrawn : 0;
        _warmUpQueued = false;
        switch (_phase)
        {
            case Phase.DisableDepth:
                StartWarmUp(nowMs);
                return new CaptureStep.DisableDepth(_skipped);

            case Phase.WarmUp:
                return AdvanceWarmUp(drawn, loading, nowMs);

            case Phase.Restore:
                _phase = Outcome == ReShadeWarmUpOutcome.Drew ? Phase.DoneSuccess : Phase.DoneFail;
                return new CaptureStep.Restore(_skipped);

            case Phase.DoneSuccess:
                return new CaptureStep.Done(true);

            default: // Phase.DoneFail
                return new CaptureStep.Done(false);
        }
    }

    /// <summary>Reports the real render's result (the bridge's LastRender right after it) and says what to do with the
    /// photo. A render that drew nothing without a hard error is retried — back to warm-up — until the deadline.</summary>
    internal RenderVerdict AfterRender(int renderCode, long nowMs)
    {
        if (!_awaitingRender) throw new InvalidOperationException("AfterRender without a Render step.");
        _awaitingRender = false;
        _lastNowMs = nowMs;
        if (renderCode > 0) return RenderVerdict.Keep;   // Outcome is already Drew; the phase already leads to success
        if (IsHardError(renderCode))
        {
            EndWithoutReShade(ReShadeWarmUpOutcome.Error);
            return RenderVerdict.KeepWithoutReShade;
        }
        if (PastDeadline(nowMs))
        {
            EndWithoutReShade(ReShadeWarmUpOutcome.RenderDrewNothing);
            return RenderVerdict.KeepWithoutReShade;
        }
        Retries++;
        Outcome = ReShadeWarmUpOutcome.Pending;
        _phase = Phase.WarmUp;   // overrides stay off: the next attempt needs them off too
        return RenderVerdict.Retry;
    }

    private void StartWarmUp(long nowMs)
    {
        _phase = Phase.WarmUp;
        _warmUpStartMs = _lastNowMs = nowMs;
    }

    private CaptureStep AdvanceWarmUp(int drawn, bool loading, long nowMs)
    {
        if (loading) _loadingMs += Math.Max(0, nowMs - _lastNowMs);
        _lastNowMs = nowMs;

        if (drawn > 0 && !loading)
        {
            _phase = _skipped.Count > 0 ? Phase.Restore : Phase.DoneSuccess;
            (Outcome, WarmUpEndCode) = (ReShadeWarmUpOutcome.Drew, drawn);
            _awaitingRender = true;
            return new CaptureStep.Render();
        }

        var error = IsHardError(drawn);
        if (error || PastDeadline(nowMs))
        {
            WarmUpEndCode = drawn;
            EndWithoutReShade(error ? ReShadeWarmUpOutcome.Error : ReShadeWarmUpOutcome.TimedOut);
            return Next(drawn, loading, nowMs);   // Restore (when techniques are held off) or Done(false)
        }

        if (loading) return new CaptureStep.Wait();
        _warmUpQueued = true;
        return new CaptureStep.WarmUp();
    }

    private bool PastDeadline(long nowMs)
    {
        var total = nowMs - _warmUpStartMs;
        return total - _loadingMs >= WarmUpTimeoutMs || total >= MaxWaitMs;
    }

    private void EndWithoutReShade(ReShadeWarmUpOutcome outcome)
    {
        Outcome = outcome;
        _phase = _skipped.Count > 0 ? Phase.Restore : Phase.DoneFail;
    }

    private static IReadOnlyList<ReShadeTechniqueRef> CollectSkipped(IReadOnlyList<ReShadeTechnique> active, bool shaped, bool skipSizeLocked)
    {
        List<ReShadeTechniqueRef>? names = null;
        foreach (var technique in active)
        {
            if (!Skips(technique, shaped, skipSizeLocked)) continue;
            names ??= new List<ReShadeTechniqueRef>();
            names.Add(new ReShadeTechniqueRef(technique.EffectFile, technique.Name));
        }
        return (IReadOnlyList<ReShadeTechniqueRef>?)names ?? Array.Empty<ReShadeTechniqueRef>();
    }
}
