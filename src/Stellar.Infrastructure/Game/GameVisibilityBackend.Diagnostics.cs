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
    /// (ours or the game's own) is visible from the log alone. Resolves ZEntityMgr/getHideCount itself through the
    /// shared <see cref="EnsureHoldCountReflection"/> helper (also used by <c>HoldCount</c>) rather than depending on
    /// <see cref="_getHideCount"/> / <see cref="_hideTypeEnum"/> / <see cref="_holdCountSource"/> having already been
    /// populated — Self's camera types (Oneself/SelfPet) have no <see cref="EntityShowPlan.TryHideTypeFor"/> mapping,
    /// so a Self-only session needs its own resolve. Each enum value's read is independently guarded so one bad
    /// value can't drop the whole line; the whole method is also wrapped, silent on failure.</summary>
    partial void OnSelfHoldsChanged(bool hide)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        try
        {
            if (!EnsureHoldCountReflection(out var mgr)) return;
            var parts = new List<string>();
            foreach (var value in Enum.GetValues(_hideTypeEnum!))
            {
                try
                {
                    if (_getHideCount!.Invoke(mgr, new[] { value, _holdCountSource! }) is int count && count != 0)
                        parts.Add($"{value}({Convert.ToInt32(value)})={count}");
                }
                catch { /* one bad enum value must not drop the whole line */ }
            }
            _log.Info($"[PhotoVis] holds after Self hide={hide}: {string.Join(" ", parts)}");
        }
        catch { /* diagnostics only — a reflection miss here must never surface */ }
    }
}
