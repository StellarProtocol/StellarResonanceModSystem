using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Loadout;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for <see cref="PandaLoadoutProbe"/>. Per-event dispatch /
/// result lines are gated on <see cref="StellarDiagnostics.IsEnabled"/>; the one-shot
/// bridge-resolution line fires unconditionally so the scenario gates have evidence the
/// Lua bridge resolved even on a non-diagnostic run.
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    private int _failedResolutionAttempts;
    private const int ResolutionFailureLogEvery = 60;

    private bool _equipProbeLogged;

    // Per-class gear RE (2026-08-03): run the equip-structure ProbeChunk ONCE and log the dumped
    // runtime shape (CharSerialize equip containers + each plan's equip reference), so the project ->
    // equip-set mapping can be nailed and the per-class gear decoded framework-side. Diagnostics-gated;
    // called from ParseLoadoutData once the role-plan data is confirmed populated (so rolePlanServerData_
    // / CharSerialize are non-empty). Read-only probe.
    private void LogEquipProbe()
    {
        if (!StellarDiagnostics.IsEnabled || _equipProbeLogged) return;
        _equipProbeLogged = true;
        InvokeChunk(ProbeChunk);
        var dump = ReadLuaGlobalString(EquipProbeGlobal);
        _log.Info("[EquipProbe]\n" + (string.IsNullOrEmpty(dump) ? "(empty — no equip fields resolved)" : dump));
    }

    // Always-on one-shot: proves the Lua-bridge reflection targets resolved.
    private void OnResolutionSucceeded()
    {
        _log.Info(
            "[Stellar][Loadout] resolved switch bridge: tolua# LuaState.mainState + DoString" +
            "; apply via weapon VM wrapper AsyncSwitchRolePlan (Role Plan)");
    }

    private void OnResolutionFailure(string reason)
    {
        _failedResolutionAttempts++;
        if (!_resolutionFailureLogged)
        {
            _resolutionFailureLogged = true;
            _log.Warning($"[Stellar][Loadout] bridge not resolved: {reason}");
            return;
        }
        if (_failedResolutionAttempts % ResolutionFailureLogEvery == 0)
        {
            _log.Warning($"[Stellar][Loadout] bridge still not resolved ({_failedResolutionAttempts} attempts): {reason}");
        }
    }

    private void DiagDispatched(int planId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][Loadout] AsyncSwitchRolePlan(planId={planId}) dispatched");
    }

    private void DiagResult(int planId, LoadoutResult result, long elapsedMs)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][Loadout] switch(id={planId}) result: {result} after {elapsedMs}ms");
    }
}
