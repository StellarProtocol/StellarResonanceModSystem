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
    private bool _perClassResolvedLogged;


    // Per-class gear/module resolution result (2026-08-03): logs each plan's decoded gear/module counts
    // + the first gear piece's roll counts, so the owner's verification run also confirms the decode
    // (empty rolls ⇒ an EquipAttr property-name mismatch to fix; distinct counts ⇒ per-class working).
    // One-shot; no-op unless STELLAR_DIAGNOSTICS.
    private void LogPerClassResolved(IReadOnlyList<LoadoutEntry> entries)
    {
        if (!StellarDiagnostics.IsEnabled || _perClassResolvedLogged) return;
        _perClassResolvedLogged = true;
        var sb = new StringBuilder("[PerClassLoadout] resolved:\n");
        foreach (var e in entries)
        {
            sb.Append("  plan ").Append(e.Index).Append(" prof=").Append(e.ProfessionId)
              .Append(": gear=").Append(e.Gear?.Count ?? 0)
              .Append(" modules=").Append(e.Modules?.Count ?? 0);
            if (e.Gear is { Count: > 0 })
            {
                var g = e.Gear[0];
                sb.Append(" | gear[0] slot=").Append(g.Slot).Append(" cfg=").Append(g.ConfigId)
                  .Append(" q=").Append(g.Quality)
                  .Append(" rolls(b/a/r/rare)=").Append(g.Attrs.Basic.Count).Append('/').Append(g.Attrs.Advanced.Count)
                  .Append('/').Append(g.Attrs.Recast.Count).Append('/').Append(g.Attrs.Rare.Count)
                  .Append(" perf=").Append(g.Perfection.Value);
            }
            foreach (var kv in e.Modules ?? EmptyModulesForDiag)
            {
                sb.Append(" | mod[").Append(kv.Key).Append("] cfg=").Append(kv.Value.ConfigId)
                  .Append(" parts=").Append(kv.Value.Parts.Count);
                break;
            }
            sb.Append('\n');
        }
        _log.Info(sb.ToString());
    }

    private static readonly IReadOnlyDictionary<int, Stellar.Abstractions.Domain.Inventory.ModuleInfo> EmptyModulesForDiag
        = new Dictionary<int, Stellar.Abstractions.Domain.Inventory.ModuleInfo>(0);

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
