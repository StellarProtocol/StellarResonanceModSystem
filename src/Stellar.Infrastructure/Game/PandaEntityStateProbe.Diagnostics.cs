using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostics for <see cref="PandaEntityStateProbe"/>. One UNGATED, bounded-per-scene
/// instrument survives the 2026-07-28 cut-down to the two field-supported sites
/// (<c>recon/entity-state-death-signal-notes.md</c>): every logged Dead/Breaking
/// observation names which patch site produced it (<c>ZStateDead</c> / <c>ZStateBreaking</c>)
/// — this is the exact line the owner's next raid needs, to answer whether a scripted
/// 1%-kill boss also enters <c>ZStateDead</c>.
///
/// <para>
/// The companion "unfiltered raw-transition" instrument and the cross-site de-dup map from
/// the diagnostic round are GONE, not because either failed — the raw dump correctly showed
/// <c>EnterState</c> resolving <c>Dead</c> for every one of the ten deaths (ordinary `to=0`
/// lines were just entities settling into the Default state, not a defect), and the de-dup
/// correctly suppressed that agreeing duplicate — but because their job (identify a live,
/// affordable site) is done, and this file no longer patches anything that fires often
/// enough to need either.
/// </para>
///
/// <para>
/// The per-scene budget (<see cref="ResetObservationBudget"/>, called from
/// <c>BootstrapPlugin.OnEnterScene</c>) is unchanged: a session-lifetime counter would let
/// ordinary earlier-session play spend the budget before the raid that matters even starts.
/// Reaching the cap is ALSO logged ungated, once per scene per kind, so silence is never
/// ambiguous. Per-event detail beyond the cap, and raise-failure traces, stay gated on
/// <see cref="StellarDiagnostics.IsEnabled"/>.
/// </para>
/// </summary>
internal sealed partial class PandaEntityStateProbe
{
    private int _deadLogCountThisScene;
    private int _breakingLogCountThisScene;
    private bool _deadCapLoggedThisScene;
    private bool _breakingCapLoggedThisScene;
    private const int FirstObservedLogCapPerScene = 10;

    /// <summary>
    /// Clears both per-kind counters and their cap-reached latches. Called once per
    /// <c>OnEnterScene</c> so every dungeon/raid instance gets a fresh ungated allowance —
    /// see the type-level remarks above for why a session-lifetime budget was wrong.
    /// </summary>
    public void ResetObservationBudget()
    {
        _deadLogCountThisScene = 0;
        _breakingLogCountThisScene = 0;
        _deadCapLoggedThisScene = false;
        _breakingCapLoggedThisScene = false;
    }

    private void DiagFirstObserved(string site, ActorState state, EntityId entityId)
    {
        switch (state)
        {
            case ActorState.Dead:
                LogFirstObservedOrCapReached(site, entityId, "Dead", ref _deadLogCountThisScene, ref _deadCapLoggedThisScene);
                break;
            case ActorState.Breaking:
                LogFirstObservedOrCapReached(site, entityId, "Breaking", ref _breakingLogCountThisScene, ref _breakingCapLoggedThisScene);
                break;
        }
        DiagPerEvent(site, state, entityId);
    }

    private void LogFirstObservedOrCapReached(string site, EntityId entityId, string label, ref int countThisScene, ref bool capLogged)
    {
        if (countThisScene < FirstObservedLogCapPerScene)
        {
            countThisScene++;
            _log.Info($"[EntityState] site={site} entity={entityId.Value} entered {label} (#{countThisScene}/{FirstObservedLogCapPerScene} this scene)");
            return;
        }
        if (capLogged) return;
        capLogged = true;
        _log.Info($"[EntityState] {label} observation budget spent for this scene ({FirstObservedLogCapPerScene} logged); " +
                  "further observations are silent unless StellarDiagnostics is enabled. Resets on the next scene/dungeon entry.");
    }

    private void DiagPerEvent(string site, ActorState state, EntityId entityId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[EntityStateDbg] site={site} entity={entityId.Value} state={state}");
    }

    // Gated (not ungated) — a marshal/reflection failure on a hot per-transition path could
    // repeat every time this entity re-enters the state; an ungated log here risks the exact
    // spam the per-scene cap above exists to avoid on the success path.
    private void DiagRaiseFailed(string site, ActorState state, System.Exception ex)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Warning($"[EntityStateDbg] site={site} raise failed for {state}: {ex.GetType().Name}: {ex.Message}");
    }
}
