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
    // raw=null (point 3/4 fix, sea/WwLG5Bq4ni) means extraction found no usable value at all — logged
    // distinctly from a genuine 0.0 reading, which the old float-defaulting-to-0 version couldn't do.
    private readonly System.Collections.Generic.HashSet<long> _rawPercentLogged = new();
    private readonly object _rawPercentLock = new();

    private void DiagRawBloodPercent(long uuid, float? raw)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        lock (_rawPercentLock)
        {
            if (!_rawPercentLogged.Add(uuid)) return;
        }
        var normalized = raw is null ? "na" : ToPercentInt(raw.Value).ToString();
        _log.Info($"[BossHp] rawPercent eid={uuid} raw={(raw is null ? "na" : raw.Value.ToString())} " +
                  $"normalized={normalized} (settles 0..100 vs 0..1 scale)");
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

    // Point 5 fix (sea/WwLG5Bq4ni, "discovery diagnostics"): one-shot per raw invoke-result Type, naming
    // the interop shape actually observed — settles whether ConversionBloodLogicDataToViewData really
    // returns a Nullable-wrapper object (point 1) without another code round-trip. Companion to
    // DiagBloodFieldsDiscovered below, which covers the (possibly different, post-unwrap) value Type's
    // field shape — the two correlate via their logged Type names.
    private void DiagNullableShapeDiscovered(System.Type t, NullableWrapperHandles handles)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[BossHp] resultType raw={t.FullName} isNullableWrapper={handles.IsNullableWrapper} " +
                  $"hasValueSrc={MemberSrc(handles.HasValueProperty is not null, handles.HasValueField is not null)} " +
                  $"valueSrc={MemberSrc(handles.ValueProperty is not null, handles.ValueField is not null)}");
    }

    // Companion to DiagNullableShapeDiscovered — one-shot per (unwrapped) value Type, naming how
    // BloodPercent/Stage actually resolved (field vs property vs missing).
    private void DiagBloodFieldsDiscovered(System.Type t, BloodFieldHandles handles)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[BossHp] resultType value={t.FullName} " +
                  $"percent={MemberSrc(handles.PercentProperty is not null, handles.PercentField is not null)} " +
                  $"stage={MemberSrc(handles.StageProperty is not null, handles.StageField is not null)}");
    }

    private static string MemberSrc(bool viaProperty, bool viaField)
        => viaProperty ? "property" : viaField ? "field" : "missing";

    // Point 3 fix (sea/WwLG5Bq4ni): a native read that came back 0% is treated as a non-observation in
    // TryGetBlood (not cached, no watcher started) — logs the decision so the acceptance raid can see
    // how often this actually fires, distinct from a genuine failed live read (which never reaches here).
    private void DiagNativeZeroSuppressed(EntityId id, int stage)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[BossHp] native eid={id.Value} pct=0 stage={stage} SUPPRESSED (treated as non-observation, falling back to cache)");
    }
}
