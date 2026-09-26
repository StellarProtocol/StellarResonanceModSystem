using System;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Application.Services;

internal sealed partial class SpecRootBuffMap
{
    private void DiagFallback(Dictionary<int, int>? derived)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var why = derived is null ? "talent tables unavailable" : $"derived {derived.Count} entries, expected {SpecRootBuffs.ExpectedCount}";
        _log.Info($"[CombatSpec] spec root buffs fallback: {why}");
    }

    private void DiagDeriveThrew(Exception ex)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[CombatSpec] spec root buff derivation threw: {ex.GetType().Name}: {ex.Message}");
    }
}
