using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// Covers the magnitude gate that maps an enter-scene <c>AttrSceneUuid</c> to the
/// run id. The load-bearing invariant for the run-identity collision fix: a
/// below-floor (non-instanced) scene resolves to 0 so the previous dungeon's id
/// cannot linger onto a later open-world run.
/// </summary>
public sealed class DungeonRunIdGateTests
{
    // Real values observed in-game (BepInEx enter-scene diagnostic).
    private const long WorldDominatorInstanceUuid = 1003770480261332992L; // instanced world boss
    private const long DungeonInstanceUuid        = 493733355695636480L;  // instanced dungeon
    private const long OpenWorldFieldUuid         = 281874408669184L;     // Bahamar Highlands field zone
    private const long TownUuid                    = 281509336449024L;    // town/hub

    [Theory]
    [InlineData(WorldDominatorInstanceUuid)]
    [InlineData(DungeonInstanceUuid)]
    public void Resolve_InstancedSnowflake_ReturnsItAsRunId(long sceneUuid)
        => Assert.Equal(sceneUuid, DungeonRunIdGate.Resolve(sceneUuid));

    [Theory]
    [InlineData(OpenWorldFieldUuid)]
    [InlineData(TownUuid)]
    [InlineData(0L)]
    public void Resolve_NonInstancedScene_ReturnsZero(long sceneUuid)
        => Assert.Equal(0L, DungeonRunIdGate.Resolve(sceneUuid));

    [Fact]
    public void Resolve_AtFloor_IsNonInstanced()
    {
        // The floor itself is exclusive — only strictly-above counts as instanced.
        Assert.Equal(0L, DungeonRunIdGate.Resolve(DungeonRunIdGate.DungeonInstanceUuidFloor));
        Assert.Equal(
            DungeonRunIdGate.DungeonInstanceUuidFloor + 1,
            DungeonRunIdGate.Resolve(DungeonRunIdGate.DungeonInstanceUuidFloor + 1));
    }
}
