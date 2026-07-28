using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostics for <see cref="PandaEntityStateProbe"/>. The first
/// <see cref="FirstObservedLogCapPerScene"/> observations of EACH kind (Dead / Breaking) are
/// logged UNGATED per framework policy (boot/one-shot lines always fire) — verification
/// without a raid (2026-07-28 spec § 4): a single trash-mob death in a short dungeon
/// already proves the plumbing, and the owner's NEXT raid answers "does a scripted-kill
/// boss reach Dead" from one plain log line with no scheduled test.
///
/// <para>
/// <b>2026-07-28 review fix — budget is PER SCENE, not per session.</b> A session-lifetime
/// counter (the first cut of this file, modelled on <c>PandaCombatStubProbe.DamageLogCap</c>)
/// is the wrong shape here: that cap answers "is the wire live", a check that fires many
/// times a second, so burning it in seconds is fine. Deaths are orders of magnitude rarer —
/// ten ordinary mob deaths earlier in the SAME session (any dungeon run before the one that
/// matters) would silently spend the whole budget before the owner's raid even starts, and
/// the only fallback (<c>STELLAR_DIAGNOSTICS</c>) needs an env var AND a restart set up
/// BEFORE the raid — exactly the scheduled test this design exists to avoid. Resetting on
/// every <c>OnEnterScene</c> (<see cref="ResetObservationBudget"/>, called from
/// <c>BootstrapPlugin.OnEnterScene</c>) gives every dungeon/raid instance — including one
/// started an hour into a session — its own fresh allowance.
/// </para>
///
/// <para>
/// Every observation beyond the per-scene cap (and the raise-failure trace) is gated on
/// <see cref="StellarDiagnostics.IsEnabled"/>. Reaching the cap itself is ALSO logged
/// ungated, once per scene per kind, so silence is never ambiguous — a raid that hits an
/// exhausted budget still tells the owner why nothing more is printing, without them having
/// to already suspect a cap exists.
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

    private void DiagFirstObserved(ActorState state, EntityId entityId)
    {
        switch (state)
        {
            case ActorState.Dead:
                LogFirstObservedOrCapReached(entityId, "Dead", ref _deadLogCountThisScene, ref _deadCapLoggedThisScene);
                break;
            case ActorState.Breaking:
                LogFirstObservedOrCapReached(entityId, "Breaking", ref _breakingLogCountThisScene, ref _breakingCapLoggedThisScene);
                break;
        }
        DiagPerEvent(state, entityId);
    }

    private void LogFirstObservedOrCapReached(EntityId entityId, string label, ref int countThisScene, ref bool capLogged)
    {
        if (countThisScene < FirstObservedLogCapPerScene)
        {
            countThisScene++;
            _log.Info($"[EntityState] entity {entityId.Value} entered {label} (#{countThisScene}/{FirstObservedLogCapPerScene} this scene)");
            return;
        }
        if (capLogged) return;
        capLogged = true;
        _log.Info($"[EntityState] {label} observation budget spent for this scene ({FirstObservedLogCapPerScene} logged); " +
                  "further observations are silent unless StellarDiagnostics is enabled. Resets on the next scene/dungeon entry.");
    }

    private void DiagPerEvent(ActorState state, EntityId entityId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[EntityStateDbg] entity={entityId.Value} state={state}");
    }

    // Gated (not ungated) — a marshal/reflection failure on a hot per-transition path could
    // repeat every time this entity re-enters the state; an ungated log here risks the exact
    // spam the per-scene cap above exists to avoid on the success path.
    private void DiagRaiseFailed(ActorState patchedState, System.Exception ex)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Warning($"[EntityStateDbg] raise failed for {patchedState}: {ex.GetType().Name}: {ex.Message}");
    }
}
