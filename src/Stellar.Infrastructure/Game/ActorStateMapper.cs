using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

// Maps a raw Zproto.EActorState wire int (data/StarResonanceData/proto/zproto/enum_e_actor_state.proto)
// to the Abstractions ActorState subset Stellar consumes. Unknown/future wire codes map to
// ActorState.Unknown rather than throwing — same tolerance policy as PartyService.MapLeaveKind.
// Pure and fully unit-testable (ActorStateMapperTests); mirrors the DungeonRunIdGate precedent of a
// small internal-static Infrastructure mapper that Stellar.Application.Tests exercises directly via
// the InternalsVisibleTo grant (Stellar.Infrastructure.csproj).
//
// PandaEntityStateProbe uses this defensively, not as its primary source of truth: which concrete
// OnEnter fired (EntityCtrlDead ⇒ Dead, ZStateBreaking ⇒ Breaking) already tells it the answer, since
// those two types exist for exactly one state each. Mapping the game's own EActorState argument is a
// cross-check that costs nothing when it agrees and never mis-signals when it can't be read.
internal static class ActorStateMapper
{
    public static ActorState MapWireValue(int raw) => raw switch
    {
        9  => ActorState.Dead,
        23 => ActorState.Breaking,
        _  => ActorState.Unknown,
    };
}
