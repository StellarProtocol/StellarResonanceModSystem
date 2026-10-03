using System;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated per-layer logging for the visibility backend.</summary>
internal sealed partial class GameVisibilityBackend
{
    private void OnLayerSet(VisibilityLayers layer, bool hide, bool ok)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoVis] {layer} hide={hide} ok={ok} applied={_applied}");
    }

    private void OnEntityShowWritten(int type, bool show)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoVis] SetEntityShow({type},{show}) by Stellar");
    }

    /// <summary>Owner's in-game pass could not be run for this change — this line replaces it: every NON-ZERO
    /// ETakePhotos hold count over all EntityRenderLayerHideType values right after a Self write, so a stray hold
    /// (ours or the game's own) is visible from the log alone. Fix round 1: calls the same
    /// <see cref="EnsureHoldCountReflection"/> helper <c>HoldCount</c> uses, instead of requiring an earlier
    /// OtherPlayers/party hide to have already populated <see cref="_getHideCount"/> / <see cref="_hideTypeEnum"/> /
    /// <see cref="_holdCountSource"/> — Self's camera types (Oneself/SelfPet) have no
    /// <see cref="EntityShowPlan.TryHideTypeFor"/> mapping, so a Self-only session never resolved them before this
    /// fix, and the measurement was silently lost. Whole thing in try/catch, silent on failure.</summary>
    partial void OnSelfHoldsChanged(bool hide)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        try
        {
            if (!EnsureHoldCountReflection(out var mgr)) return;
            var parts = new List<string>();
            foreach (var value in Enum.GetValues(_hideTypeEnum!))
            {
                if (_getHideCount!.Invoke(mgr, new[] { value, _holdCountSource! }) is int count && count != 0)
                    parts.Add($"{value}({Convert.ToInt32(value)})={count}");
            }
            _log.Info($"[PhotoVis] holds after Self hide={hide}: {string.Join(" ", parts)}");
        }
        catch { /* diagnostics only — a reflection miss here must never surface */ }
    }
}
