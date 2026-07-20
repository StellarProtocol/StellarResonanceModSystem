using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="EntityTransformsService"/>'s wire-position fallback.
/// The first engagement per session is logged UNGATED (boot/one-shot lines always fire — framework
/// policy) so the owner's validation raid run shows the fallback firing in the plain log without
/// <c>STELLAR_DIAGNOSTICS</c>; every subsequent engagement is per-event and gated on the toggle.
/// </summary>
internal sealed partial class EntityTransformsService
{
    // One-shot latch for the ungated "first fallback engaged" line.
    private bool _firstFallbackLogged;

    private void DiagWireFallbackEngaged(EntityId id, long ageMs)
    {
        if (!_firstFallbackLogged)
        {
            _firstFallbackLogged = true;
            _log.Info($"[Transforms] wire-position fallback engaged for entity {id.Value} at +{ageMs} ms " +
                      "(GO transform degenerate: zero-sentinel or Y-floor disagreement; substituted cached AttrPos)");
        }
        DiagWireFallbackPerEvent(id, ageMs);
    }

    private void DiagWireFallbackPerEvent(EntityId id, long ageMs)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PosDbg][fallback] id={id.Value} ageMs={ageMs} (GO degenerate -> wire AttrPos)");
    }
}
