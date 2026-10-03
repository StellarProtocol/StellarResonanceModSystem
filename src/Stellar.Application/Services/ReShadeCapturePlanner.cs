using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// One step of a <see cref="ReShadeCapturePlanner"/> plan. <see cref="Done"/> is absorbing — once returned, the
/// planner returns the same <see cref="Done"/> forever.
/// </summary>
internal abstract record CaptureStep
{
    /// <summary>Let ReShade draw one more frame; keep calling until the queued render reports it drew something.</summary>
    internal sealed record WarmUp : CaptureStep;
    /// <summary>Turn off the named depth-using techniques for this capture (shaped captures only, once).</summary>
    internal sealed record DisableDepth(IReadOnlyList<string> Names) : CaptureStep;
    /// <summary>Queue the actual capture render. Returned at most once per plan.</summary>
    internal sealed record Render : CaptureStep;
    /// <summary>Turn the named techniques back on (mirrors an earlier <see cref="DisableDepth"/>).</summary>
    internal sealed record Restore(IReadOnlyList<string> Names) : CaptureStep;
    /// <summary>The plan is finished. <see cref="Applied"/> is false when nothing was drawn (no active
    /// techniques, or the warm-up timed out).</summary>
    internal sealed record Done(bool Applied) : CaptureStep;
}

/// <summary>
/// Pure per-capture state machine deciding what the ReShade capture pass should do next, one call per frame.
/// Construct one instance per capture; it holds no reference to anything renderable. No I/O, no threading.
/// </summary>
internal sealed class ReShadeCapturePlanner
{
    /// <summary>How long a plan may sit in warm-up (no drawn frame yet) before it gives up.</summary>
    internal const long WarmUpTimeoutMs = 5000;

    private enum Phase
    {
        DisableDepth,
        WarmUp,
        Restore,
        DoneSuccess,
        DoneFail,
    }

    private readonly IReadOnlyList<string> _depthNames;
    private Phase _phase;
    private long _warmUpStartMs;

    /// <summary>
    /// <paramref name="active"/> must already be the active techniques (<c>Enabled == true</c>). When
    /// <paramref name="shaped"/> and one or more of them have <c>UsesDepth == true</c>, the plan disables those
    /// first and restores them after the render; otherwise it warms up and renders directly.
    /// </summary>
    internal ReShadeCapturePlanner(bool shaped, IReadOnlyList<ReShadeTechnique> active, long nowMs)
    {
        if (active.Count == 0)
        {
            _depthNames = Array.Empty<string>();
            _phase = Phase.DoneFail;
            return;
        }

        _depthNames = shaped ? CollectDepthNames(active) : Array.Empty<string>();
        if (_depthNames.Count > 0)
        {
            _phase = Phase.DisableDepth;
        }
        else
        {
            _phase = Phase.WarmUp;
            _warmUpStartMs = nowMs;
        }
    }

    /// <summary>Advances the plan by one frame. <paramref name="lastDrawn"/> is how many techniques the previous
    /// frame's queued render drew (0 when nothing has been drawn yet).</summary>
    internal CaptureStep Next(int lastDrawn, long nowMs)
    {
        switch (_phase)
        {
            case Phase.DisableDepth:
                _phase = Phase.WarmUp;
                _warmUpStartMs = nowMs;
                return new CaptureStep.DisableDepth(_depthNames);

            case Phase.WarmUp:
                return AdvanceWarmUp(lastDrawn, nowMs);

            case Phase.Restore:
                _phase = Phase.DoneSuccess;
                return new CaptureStep.Restore(_depthNames);

            case Phase.DoneSuccess:
                return new CaptureStep.Done(true);

            default: // Phase.DoneFail
                return new CaptureStep.Done(false);
        }
    }

    private CaptureStep AdvanceWarmUp(int lastDrawn, long nowMs)
    {
        if (lastDrawn > 0)
        {
            _phase = _depthNames.Count > 0 ? Phase.Restore : Phase.DoneSuccess;
            return new CaptureStep.Render();
        }

        if (nowMs - _warmUpStartMs >= WarmUpTimeoutMs)
        {
            _phase = Phase.DoneFail;
            return _depthNames.Count > 0
                ? new CaptureStep.Restore(_depthNames)
                : new CaptureStep.Done(false);
        }

        return new CaptureStep.WarmUp();
    }

    private static IReadOnlyList<string> CollectDepthNames(IReadOnlyList<ReShadeTechnique> active)
    {
        List<string>? names = null;
        foreach (var technique in active)
        {
            if (!technique.UsesDepth) continue;
            names ??= new List<string>();
            names.Add(technique.Name);
        }
        return (IReadOnlyList<string>?)names ?? Array.Empty<string>();
    }
}
