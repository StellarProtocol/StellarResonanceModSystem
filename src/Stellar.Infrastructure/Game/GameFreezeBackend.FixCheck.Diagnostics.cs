using System;
using Stellar.Abstractions.Diagnostics;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>Diagnostics proof for the combat-freeze fix (owner MAIN evidence 2026-10-02; diagnostics only — every partial
/// returns on its first line unless <c>StellarDiagnostics.IsEnabled</c>). Per sampled monster, on its <c>e</c> line:
/// <c>fix: dRot=&lt;°&gt; ecs=&lt;uid|-&gt; ecsTracked=&lt;y|n&gt; ecsHeld=&lt;n&gt; ecsLeak=&lt;n&gt;</c> — the drawn rotation against
/// the held one (read before our hold write, like <c>dHold</c>), and the ECS layer gate's view of the model. Per entity on
/// unfreeze: <c>fix: maxRotOff= rotOff= ecs= ecsHeld= ecsLeak= verdict=HELD|OFF:pos,rot,ecs</c>. Event lines:
/// <c>[FreeCam] removal deferred / replayed / superseded</c>, <c>[FreeCam] deferred removals: …</c> and
/// <c>[FreeCam] freeze ecs: …</c> on unfreeze.</summary>
internal sealed partial class GameFreezeBackend
{
    private const int RemovalLinesPerFreeze = 40;
    private int _diagEcsReleased, _diagEcsReleaseWrites, _diagFxInitFrozen, _diagRemovalLines, _diagEcsTrackedAtEnd;

    /// <summary>The <c>fix:</c> segment of one entity's <c>e</c> line; updates its row. Bumps nothing when all held.</summary>
    private string DiagFixCheck(DiagCand c, PoseHold<Vector3, Quaternion>.Entry? held, out bool off)
    {
        var r = c.R;
        var row = c.Row;
        var dRot = held is { Rot: { } hr } && r.DrawnRot is { } dr
            ? FreezeDiagVerdict.AngleDegrees((dr.x, dr.y, dr.z, dr.w), (hr.x, hr.y, hr.z, hr.w)) : float.NaN;
        var rotOff = dRot > FreezeDiagVerdict.RotationEpsilonDeg;
        if (rotOff) { row.RotOff++; row.MaxRotOff = Math.Max(row.MaxRotOff, dRot); }
        var ecsModel = r.EcsUid != 0;
        var tracked = ecsModel && _ecsGate.Tracks(r.EcsUid);
        row.EcsModel |= ecsModel;
        row.EcsUntracked |= ecsModel && !tracked;
        row.EcsLeaked = _ecsGate.LeakedFor(c.Uuid);
        off = rotOff || (ecsModel && (!tracked || row.EcsLeaked > 0));
        return $"dRot={F(dRot)} ecs={(ecsModel ? r.EcsUid.ToString() : "-")} ecsTracked={YN(tracked)} " +
               $"ecsHeld={_ecsGate.HeldFor(c.Uuid)} ecsLeak={row.EcsLeaked}";
    }

    private string DiagFixSummary(FreezeDiagRow r) =>
        $"maxRotOff={r.MaxRotOff:F1} rotOff={r.RotOff} ecs={(r.EcsModel ? (r.EcsUntracked ? "untracked" : "y") : "-")} " +
        $"ecsHeld={_ecsGate.HeldFor(r.Uuid)} ecsLeak={r.EcsLeaked} " +
        $"verdict={FreezeDiagVerdict.EntityVerdict(r.MaxOffHold, r.MaxRotOff, r.EcsModel, r.EcsUntracked, r.EcsLeaked)}";

    private string DiagFixTotals() =>
        $"ecsPatched={_ecsPatched} ecsTracked={_ecsGate.Tracked} ecsHeld={_ecsGate.Held} ecsLeak={_ecsGate.Leaked} " +
        $"deferred={_removals.Deferred} queued={_removals.Count} fxInitFrozen={_diagFxInitFrozen}";

    partial void OnEcsReleased(long uuid, int writes)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _diagEcsReleased++;
        _diagEcsReleaseWrites += writes;
    }

    partial void OnEffectInitFrozen()
    {
        if (StellarDiagnostics.IsEnabled) _diagFxInitFrozen++;
    }

    partial void OnRemovalDecision(long uuid, string? type, DeferredRemovals.Decision decision)
    {
        if (!StellarDiagnostics.IsEnabled || decision == DeferredRemovals.Decision.Run) return;
        var what = decision == DeferredRemovals.Decision.Defer ? "deferred" : "superseded (the game insists: removed now)";
        RemovalLine($"removal {what}: uuid={uuid} type={type} queued={_removals.Count} k={_entities.Kind(uuid)}");
    }

    partial void OnRemovalReplayed(long uuid, string why)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        RemovalLine($"removal replayed: uuid={uuid} ({why}) inDict={YN(_entities.EntityByUuid(uuid) is not null)}");
    }

    partial void OnDeferredFlushed(string why, int replayed)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] deferred removals: replayed={replayed} ({why}) deferredThisFreeze={_removals.Deferred}");
    }

    /// <summary>At unfreeze, before any restore (from <c>OnUnfreezing</c>).</summary>
    private void DiagFixBegin() => _diagEcsTrackedAtEnd = _ecsGate.Tracked;

    /// <summary>The <c>freeze ecs:</c> line, after the restores (from <c>OnUnfrozen</c>).</summary>
    private void DiagFixEnd()
    {
        _log.Info($"[FreeCam] freeze ecs: patched={_ecsPatched} tracked={_diagEcsTrackedAtEnd} held={_ecsGate.Held} leaked={_ecsGate.Leaked} " +
                  $"restored={_diagEcsReleased} restoreWrites={_diagEcsReleaseWrites} fxInitFrozen={_diagFxInitFrozen} " +
                  $"deferred={_removals.Deferred} replayed={_removals.Replayed}");
        _diagEcsReleased = _diagEcsReleaseWrites = _diagFxInitFrozen = _diagRemovalLines = 0;
    }

    private void RemovalLine(string what)
    {
        if (_diagRemovalLines++ < RemovalLinesPerFreeze) _log.Info($"[FreeCam] {what} t={Environment.TickCount64 - _frozenAtMs}");
    }
}
