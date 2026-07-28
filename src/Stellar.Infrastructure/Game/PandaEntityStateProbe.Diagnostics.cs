using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostics for <see cref="PandaEntityStateProbe"/>. The first
/// <see cref="FirstObservedLogCap"/> observations of EACH kind (Dead / Breaking) are
/// logged UNGATED per framework policy (boot/one-shot lines always fire) — verification
/// without a raid (2026-07-28 spec § 4): a single trash-mob death in a short dungeon
/// already proves the plumbing, and the owner's NEXT raid answers "does a scripted-kill
/// boss reach Dead" from one plain log line with no scheduled test. Capped like
/// PandaCombatStubProbe's DamageLogCap so a full dungeon clear can't flood the log with
/// one line per trash-mob death; every observation beyond the cap (and the raise-failure
/// trace) is gated on <see cref="StellarDiagnostics.IsEnabled"/>.
/// </summary>
internal sealed partial class PandaEntityStateProbe
{
    private int _deadLogCount;
    private int _breakingLogCount;
    private const int FirstObservedLogCap = 10;

    private void DiagFirstObserved(ActorState state, EntityId entityId)
    {
        switch (state)
        {
            case ActorState.Dead:
                if (_deadLogCount < FirstObservedLogCap)
                {
                    _deadLogCount++;
                    _log.Info($"[EntityState] entity {entityId.Value} entered Dead (#{_deadLogCount}/{FirstObservedLogCap})");
                }
                break;
            case ActorState.Breaking:
                if (_breakingLogCount < FirstObservedLogCap)
                {
                    _breakingLogCount++;
                    _log.Info($"[EntityState] entity {entityId.Value} entered Breaking (#{_breakingLogCount}/{FirstObservedLogCap})");
                }
                break;
        }
        DiagPerEvent(state, entityId);
    }

    private void DiagPerEvent(ActorState state, EntityId entityId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[EntityStateDbg] entity={entityId.Value} state={state}");
    }

    // Gated (not ungated) — a marshal/reflection failure on a hot per-transition path could
    // repeat every time this entity re-enters the state; an ungated log here risks the exact
    // spam FirstObservedLogCap exists to avoid on the success path.
    private void DiagRaiseFailed(ActorState patchedState, System.Exception ex)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Warning($"[EntityStateDbg] raise failed for {patchedState}: {ex.GetType().Name}: {ex.Message}");
    }
}
