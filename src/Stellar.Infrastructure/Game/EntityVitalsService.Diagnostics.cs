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

    // M4 review fix: BloodPercent's real scale (0..100 vs 0..1) is UNVERIFIABLE headless — ToPercentInt's
    // <=1 "treat as fraction" heuristic would silently collapse a genuine 1% (the scripted-kill value)
    // into 100%. Logs the RAW pre-normalization float once per entity so the acceptance raid settles it.
    private readonly System.Collections.Generic.HashSet<long> _rawPercentLogged = new();
    private readonly object _rawPercentLock = new();

    private void DiagRawBloodPercent(long uuid, float raw)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        lock (_rawPercentLock)
        {
            if (!_rawPercentLogged.Add(uuid)) return;
        }
        _log.Info($"[BossHp] rawPercent eid={uuid} raw={raw} normalized={ToPercentInt(raw)} " +
                  "(settles 0..100 vs 0..1 scale)");
    }

    // I3 review fix: the liveness gate (IsEntityExist/IsEntityActive) is now mandatory — if either
    // handle never resolves, the whole native tap stays inert. Ungated (not StellarDiagnostics-gated,
    // like the boot one-shots elsewhere in this codebase) so the degraded state is visible in a plain
    // log even without STELLAR_DIAGNOSTICS=1.
    private bool _livenessGateMissingLogged;

    private void DiagLivenessGateMissing()
    {
        if (_livenessGateMissingLogged) return;
        _livenessGateMissingLogged = true;
        _log.Warning("[BossHp] IsEntityExist/IsEntityActive never resolved on ZEntityMgr — " +
                      "native boss-vitals tap disabled (liveness gate is mandatory, I3 review fix)");
    }
}
