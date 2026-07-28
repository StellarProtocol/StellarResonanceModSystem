using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// Covers the pure wire-int → <see cref="ActorState"/> mapping used defensively by
/// <see cref="PandaEntityStateProbe"/> (2026-07-28 entity-state-death-signal spec).
/// The mapping itself is fully testable headless; the Harmony patch that supplies its
/// input (a live IL2CPP <c>EActorState</c> argument) is not — see
/// docs/il2cpp-probing-safety.md and the probe's own report.
/// </summary>
public sealed class ActorStateMapperTests
{
    [Fact]
    public void MapWireValue_Nine_ReturnsDead()
        => Assert.Equal(ActorState.Dead, ActorStateMapper.MapWireValue(9));

    [Fact]
    public void MapWireValue_TwentyThree_ReturnsBreaking()
        => Assert.Equal(ActorState.Breaking, ActorStateMapper.MapWireValue(23));

    [Theory]
    [InlineData(0)]    // ActorStateDefault — a real proto value, but not one we name
    [InlineData(1)]    // ActorStateSinging
    [InlineData(37)]   // ActorStateAll
    [InlineData(999)]  // a future wire code this build has never seen
    [InlineData(-1)]   // never a legitimate wire value
    public void MapWireValue_AnyOtherValue_ReturnsUnknown_NeverThrows(int raw)
        => Assert.Equal(ActorState.Unknown, ActorStateMapper.MapWireValue(raw));
}
