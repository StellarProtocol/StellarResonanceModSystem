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

    partial void OnEntityFlagsReasserted(IReadOnlyList<int> target, bool written)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var flags = new List<int>();
        foreach (var type in target)
            if (EntityShowPlan.IsFlagType(type) && _entityShow.Holds(type)) flags.Add(type);
        _log.Info($"[PhotoVis] flag re-assert types=[{string.Join(",", flags)}] ok={written}");
    }

    partial void OnEntitiesSet(IReadOnlyList<int> target, bool ok)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoVis] Entities target=[{string.Join(",", target)}] ok={ok} applied={_applied}");
    }

    /// <summary>The measurement line for every world layer (it replaced the owner's in-game pass for Self in 2.16.0 and
    /// measures the 2.20.0 types the same way): every NON-ZERO ETakePhotos hold count over all EntityRenderLayerHideType
    /// values right after an entity write, so the counter a camera type drives — or that it drives none — is readable
    /// from the log alone (hide the types one at a time and diff consecutive lines). Resolves ZEntityMgr/getHideCount
    /// itself through the shared <see cref="EnsureHoldCountReflection"/> helper (also used by <c>HoldCount</c>) rather
    /// than depending on <see cref="_getHideCount"/> / <see cref="_hideTypeEnum"/> / <see cref="_holdCountSource"/>
    /// having already been populated — an unmeasured type has no <see cref="EntityShowPlan.TryHideTypeFor"/> mapping, so
    /// a session holding only such types needs its own resolve. Each enum value's read is independently guarded so one
    /// bad value can't drop the whole line; the whole method is also wrapped, silent on failure.</summary>
    partial void OnEntityHoldsChanged(IReadOnlyList<int> target)
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
            _log.Info($"[PhotoVis] holds after entity write target=[{string.Join(",", target)}]: {string.Join(" ", parts)}");
        }
        catch { /* diagnostics only — a reflection miss here must never surface */ }
    }
}
