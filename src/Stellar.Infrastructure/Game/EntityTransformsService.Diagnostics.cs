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

    // Throttle map for the skip trace (per entity, ~2 Hz) so a per-capture/per-entity log can't flood.
    private readonly System.Collections.Generic.Dictionary<long, long> _fbSkipLastMs = new();

    // Diagnostic (gated + throttled): why the wire fallback did NOT substitute for a GO read that LOOKS
    // degenerate (near the Y-floor). Fires only when |GO.Y| is small so it never floods on settled reads.
    // Distinguishes the two silent skip paths so a validation raid pins the run-specific miss (the
    // walk-in fallback failing on run sea/i3yeDnkRla while the same build fired on sea/490095...).
    private void DiagWireFallbackSkip(EntityId id, in Position3D go, bool wireHit, in WirePositionSample s, string reason)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (System.MathF.Abs(go.Y) > 10f) return;   // GO sits at a real floor — settled, not our case
        var now = System.Environment.TickCount64;
        if (_fbSkipLastMs.TryGetValue(id.Value, out var last) && now - last < 500) return;
        _fbSkipLastMs[id.Value] = now;
        if (wireHit)
            _log.Info($"[PosDbg][fbskip] id={id.Value} go=({go.X:0.0},{go.Y:0.0},{go.Z:0.0}) " +
                      $"wireHIT=({s.X:0.0},{s.Y:0.0},{s.Z:0.0}) age={s.AgeMs} reason={reason}");
        else
            _log.Info($"[PosDbg][fbskip] id={id.Value} go=({go.X:0.0},{go.Y:0.0},{go.Z:0.0}) wireMISS reason={reason}");
    }
}
