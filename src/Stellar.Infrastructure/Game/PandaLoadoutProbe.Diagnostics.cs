using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Application.Abstractions;

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
    private string? _lastPerClassSig;

    // Per-class resolution result (2026-08-03): a CONCISE per-plan gear/module summary logged each time the
    // resolve runs, DIFF-BASED (only when something changed) so a loadout switch or manual equip/de-equip
    // produces exactly one line. Format: "cur=<id> [*]p<plan>/<prof>:<G>g<M>m" — the CURRENT plan is marked
    // '*' and carries the LIVE overlay, so its gear count DROPS when you remove a piece (the manual-edit
    // confirmation); its slot list is appended so the removed slot is visible. No-op unless STELLAR_DIAGNOSTICS.
    private void LogPerClassResolved(IReadOnlyList<LoadoutEntry> entries)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var sb = new StringBuilder("[PerClassLoadout] cur=").Append(_currentId);
        foreach (var e in entries)
        {
            var cur = e.Index == _currentId;
            sb.Append(cur ? " *p" : " p").Append(e.Index).Append('/').Append(e.ProfessionId)
              .Append(':').Append(e.Gear?.Count ?? 0).Append('g').Append(e.Modules?.Count ?? 0).Append('m');
            if (cur)   // the overlaid plan — dump slot:configId for gear + modules so a SWAP (same slot, new
            {          // item) shows too, not only a de-equip (which changes the count)
                if (e.Gear is { } g)
                {
                    sb.Append(" g[");
                    foreach (var gi in g) sb.Append(gi.Slot).Append(':').Append(gi.ConfigId).Append(',');
                    sb.Append(']');
                }
                if (e.Modules is { } m)
                {
                    sb.Append("m[");
                    foreach (var kv in m) sb.Append(kv.Key).Append(':').Append(kv.Value.ConfigId).Append(',');
                    sb.Append(']');
                }
            }
        }
        var sig = sb.ToString();
        if (sig == _lastPerClassSig) return;   // diff-based: one line per actual change
        _lastPerClassSig = sig;
        _log.Info(sig);
    }

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
