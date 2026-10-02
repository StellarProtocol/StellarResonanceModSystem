using System;
using Stellar.Abstractions.Diagnostics;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>Diagnostics proof for the release half of the combat-freeze fix (review 2026-10-02; diagnostics only — every
/// partial returns on its first line unless <c>StellarDiagnostics.IsEnabled</c>).
/// <para><b>Rotation snap</b>: when the hold releases, each held entity's drawn rotation is read against its logical one
/// just BEFORE and just AFTER the snap — <c>[FreeCam] release rot: held=&lt;n&gt; sampled=&lt;n&gt; preMax=&lt;°&gt; postMax=&lt;°&gt;
/// postOff=&lt;n&gt; unreadable=&lt;n&gt;</c>. Success: <c>postMax</c> ≤ 2° and <c>postOff=0</c> (the snap landed), with
/// <c>preMax</c> showing how far the hold had kept the model from where the game had turned it.</para>
/// <para><b>Effects</b>: on the <c>freeze ecs:</c> line, <c>fxByManager= fxByInstance= fxStale= fxLost= fxMissingFromDict=</c>
/// — the ledger uids missing from <c>EffectDict</c> at unfreeze, and how each was handled.</para></summary>
internal sealed partial class GameFreezeBackend
{
    private const int ReleaseRotSampleCap = 64;
    private FreezeEffectRule.UnfreezeCounts _diagFxRelease;
    private float _diagRotPreMax;
    private int _diagRotSampled, _diagRotUnreadable;

    partial void OnEffectsUnfrozen(FreezeEffectRule.UnfreezeCounts counts)
    {
        if (StellarDiagnostics.IsEnabled) _diagFxRelease = counts;
    }

    private string DiagFxReleaseText()
    {
        var c = _diagFxRelease;
        _diagFxRelease = default;
        return $"fxByManager={c.Manager} fxByInstance={c.Instance} fxStale={c.Stale} fxLost={c.Lost} fxMissingFromDict={c.MissingFromDict}";
    }

    partial void OnHoldReleasing()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _diagRotPreMax = 0f;
        _diagRotSampled = _diagRotUnreadable = 0;
        SampleReleaseRotation(pre: true, out _, out _);
    }

    partial void OnHoldReleased()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        SampleReleaseRotation(pre: false, out var postMax, out var postOff);
        _log.Info($"[FreeCam] release rot: held={_held.Count} sampled={_diagRotSampled} preMax={_diagRotPreMax:F1} " +
                  $"postMax={postMax:F1} postOff={postOff} unreadable={_diagRotUnreadable}");
    }

    // The drawn rotation of each held entity against its logical one (both read live, through the hold's own liveness-gated
    // lookup — LogicalPose: null once it left or is destroying).
    private void SampleReleaseRotation(bool pre, out float max, out int off)
    {
        max = 0f;
        off = 0;
        if (_getRot is null) return;
        var n = Math.Min(_held.Count, ReleaseRotSampleCap);
        for (var i = 0; i < n; i++)
        {
            var d = DrawnVsLogicalDegrees(_held.Entries[i].Uuid);
            if (float.IsNaN(d)) { if (pre) _diagRotUnreadable++; continue; }
            if (pre) _diagRotSampled++;
            max = Math.Max(max, d);
            if (d > FreezeDiagVerdict.RotationEpsilonDeg) off++;
        }
        if (pre) _diagRotPreMax = max;
    }

    private float DrawnVsLogicalDegrees(long uuid)
    {
        try
        {
            if (LogicalPose(uuid) is not { Rot: { } logical } pose) return float.NaN;
            var drawn = _getRot!(pose.Comp);
            return FreezeDiagVerdict.AngleDegrees((drawn.x, drawn.y, drawn.z, drawn.w), (logical.x, logical.y, logical.z, logical.w));
        }
        catch { return float.NaN; }
    }
}
