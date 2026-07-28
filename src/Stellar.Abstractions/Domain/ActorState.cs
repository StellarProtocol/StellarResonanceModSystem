namespace Stellar.Abstractions.Domain;

/// <summary>
/// Subset of the client's actor/controller state machine (<c>Zproto.EActorState</c>,
/// see <c>data/StarResonanceData/proto/zproto/enum_e_actor_state.proto</c>) exposed on
/// <see cref="CombatEvent.EntityStateChanged"/>. Only the values Stellar currently
/// consumes are named; every other wire code — including ones the game adds in a
/// future patch — is reported as <see cref="Unknown"/> rather than throwing (same
/// tolerance policy as <see cref="PartyLeaveKind"/>). Named-value numbers are the
/// proto's own wire ints, so a caller comparing against the raw wire value never
/// needs a separate lookup table.
/// </summary>
public enum ActorState
{
    /// <summary>Wire value not one of the states named below.</summary>
    Unknown = 0,

    /// <summary>
    /// <c>ActorStateDead</c> (9) — the entity's client-side state machine entered its
    /// dead state. Raised from the <c>Panda.ZGame.EntityCtrlDead.OnEnter</c> patch
    /// (2026-07-28 entity-state-death-signal spec): the client cannot render a dead
    /// entity without entering this state, whatever caused the death — including
    /// scripted removals that never zero the target's HP.
    /// </summary>
    Dead = 9,

    /// <summary>
    /// <c>ActorStateBreaking</c> (23) — the entity entered its "break" (poise-broken)
    /// phase. Raised from the <c>Panda.ZGame.ZStateBreaking.OnEnter</c> patch; a free
    /// corroborating win from the same design (gives frametime-spike reports an exact
    /// in-log timestamp to correlate against).
    /// </summary>
    Breaking = 23,
}
