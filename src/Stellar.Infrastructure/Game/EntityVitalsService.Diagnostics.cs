using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="EntityVitalsService"/> — recon §6 grammar line 4, "the acceptance
/// test for the tap": a native read side-by-side with the wire-derived vitals
/// (<see cref="Stellar.Abstractions.Services.ICombatLookup.GetVitals"/>) so the next raid proves the
/// native tap is immune to the wire mirror's AOI-eviction starvation (L1). Gated behind
/// <c>STELLAR_DIAGNOSTICS=1</c> — zero cost in normal play.
/// </summary>
internal sealed partial class EntityVitalsService
{
    private void DiagNativeRead(EntityId id, int pct, int stage)
    {
        if (!StellarDiagnostics.IsEnabled) return;

        var wire = _combatLookup.GetVitals(id);
        string wirePct = "na";
        string delta = "na";
        if (wire.IsKnown && wire.HasHpObservation && wire.MaxHp > 0)
        {
            var wp = (int)System.Math.Round(100.0 * wire.Hp / wire.MaxHp);
            wirePct = wp.ToString();
            var d = pct - wp;
            delta = d >= 0 ? $"+{d}" : d.ToString();
        }
        _log.Info($"[BossHp] native eid={id.Value} pct={pct} stage={stage} | wire pct={wirePct} delta={delta}");
    }
}
