using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostics for <see cref="PandaEntityStateProbe"/>. Two UNGATED, bounded-per-scene
/// instruments (framework policy: boot/one-shot lines always fire), added/kept after the
/// 2026-07-28 field result (leaf patches installed cleanly and never fired — a silent
/// no-op that cost the owner a run, so this round must not repeat that silently):
///
/// <list type="number">
/// <item><b>Site-attributed first-observed lines</b> — every logged Dead/Breaking
/// observation names which patch site produced it (<c>onStateChanged</c> / <c>EnterState</c>
/// / <c>ZStateDead</c> / <c>EntityCtrlDead</c> / <c>ZStateBreaking</c>), so one run tells us
/// which sites are actually live.</item>
/// <item><b>Unfiltered raw-transition lines</b> — the first
/// <see cref="UnfilteredTransitionLogCapPerScene"/> transitions per scene at EACH machine
/// hook (<c>onStateChanged</c>/<c>EnterState</c>), logged with their RAW integer state
/// values regardless of whether they match Dead/Breaking. This is what makes the
/// <c>EActorState</c> numbering (Dead=9, Breaking=23, from
/// <c>enum_e_actor_state.proto</c>) a confirmed FACT instead of an assumption that fails
/// silently if the runtime enum ever differs — exactly the failure class that produced
/// the empty-log field result this round is fixing.</item>
/// </list>
///
/// <para>
/// Both budgets are PER SCENE (<see cref="ResetObservationBudget"/>, called from
/// <c>BootstrapPlugin.OnEnterScene</c>) — not per session — per the prior review fix: a
/// session-lifetime counter would let ordinary earlier-session play spend the budget
/// before the raid that matters even starts. Reaching either cap is ALSO logged ungated,
/// once per scene per counter, so silence is never ambiguous.
/// </para>
///
/// <para>
/// Per-event detail beyond the caps, duplicate-suppressed traces, and raise-failure traces
/// are all gated on <see cref="StellarDiagnostics.IsEnabled"/>.
/// </para>
/// </summary>
internal sealed partial class PandaEntityStateProbe
{
    private int _deadLogCountThisScene;
    private int _breakingLogCountThisScene;
    private bool _deadCapLoggedThisScene;
    private bool _breakingCapLoggedThisScene;
    private const int FirstObservedLogCapPerScene = 10;

    private int _unfilteredOnStateChangedCountThisScene;
    private int _unfilteredEnterStateCountThisScene;
    private bool _unfilteredOnStateChangedCapLoggedThisScene;
    private bool _unfilteredEnterStateCapLoggedThisScene;
    private const int UnfilteredTransitionLogCapPerScene = 12;

    /// <summary>
    /// Clears every per-scene counter/latch and the de-dup map. Called once per
    /// <c>OnEnterScene</c> so every dungeon/raid instance gets a fresh ungated allowance —
    /// see the type-level remarks above for why a session-lifetime budget was wrong.
    /// </summary>
    public void ResetObservationBudget()
    {
        _deadLogCountThisScene = 0;
        _breakingLogCountThisScene = 0;
        _deadCapLoggedThisScene = false;
        _breakingCapLoggedThisScene = false;
        _unfilteredOnStateChangedCountThisScene = 0;
        _unfilteredEnterStateCountThisScene = 0;
        _unfilteredOnStateChangedCapLoggedThisScene = false;
        _unfilteredEnterStateCapLoggedThisScene = false;
        _recentlyRaised.Clear();
    }

    // ---- Instrument 2: unfiltered raw transitions (proves the enum numbering) ----

    private void DiagUnfilteredTransition(string site, int? fromRaw, int toRaw)
    {
        if (site == SiteOnStateChanged)
        {
            LogUnfilteredOrCapReached(site, fromRaw, toRaw, ref _unfilteredOnStateChangedCountThisScene, ref _unfilteredOnStateChangedCapLoggedThisScene);
        }
        else if (site == SiteEnterState)
        {
            LogUnfilteredOrCapReached(site, fromRaw, toRaw, ref _unfilteredEnterStateCountThisScene, ref _unfilteredEnterStateCapLoggedThisScene);
        }
    }

    private void LogUnfilteredOrCapReached(string site, int? fromRaw, int toRaw, ref int countThisScene, ref bool capLogged)
    {
        if (countThisScene < UnfilteredTransitionLogCapPerScene)
        {
            countThisScene++;
            var fromText = fromRaw.HasValue ? fromRaw.Value.ToString() : "?";
            _log.Info($"[EntityStateRaw] site={site} from={fromText} to={toRaw} (#{countThisScene}/{UnfilteredTransitionLogCapPerScene} this scene, unfiltered)");
            return;
        }
        if (capLogged) return;
        capLogged = true;
        _log.Info($"[EntityStateRaw] site={site} unfiltered-transition budget spent for this scene " +
                  $"({UnfilteredTransitionLogCapPerScene} logged); further raw transitions are silent. Resets on the next scene/dungeon entry.");
    }

    // ---- Instrument 1: site-attributed first-observed Dead/Breaking (post-dedup) ----

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

    // ---- Gated detail ----

    private void DiagPerEvent(string site, ActorState state, EntityId entityId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[EntityStateDbg] site={site} entity={entityId.Value} state={state}");
    }

    // A duplicate means a SECOND site fired for a transition a FIRST site already raised —
    // informative for judging "which sites are live", but not one of the two ungated
    // instruments the review specifically asked for, so this stays gated.
    private void DiagDuplicateSuppressed(string site, ActorState state, EntityId entityId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[EntityStateDbg] site={site} entity={entityId.Value} DUPLICATE {state} suppressed (already raised for this transition)");
    }

    // Gated (not ungated) — a marshal/reflection failure on a hot per-transition path could
    // repeat every time this entity re-enters the state; an ungated log here risks the exact
    // spam the per-scene caps above exist to avoid on the success path.
    private void DiagRaiseFailed(string site, ActorState state, System.Exception ex)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Warning($"[EntityStateDbg] site={site} raise failed for {state}: {ex.GetType().Name}: {ex.Message}");
    }
}
