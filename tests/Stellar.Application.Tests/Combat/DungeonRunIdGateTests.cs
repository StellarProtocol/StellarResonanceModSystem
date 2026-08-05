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

    // -------------------------------------------------------------------------
    // Scene-KIND classification (SceneTable.SceneType via IGameDataWorld.GetScene):
    // 1 = world/town/field, 2 = instanced dungeon/raid. When the scene kind is KNOWN
    // it wins over the magnitude heuristic — the fix for ranked content (Mistveil
    // Hunting Ground) whose per-instance scene uuid sometimes lands BELOW the 2^53
    // floor and was wrongly zeroed. Unknown kind (game-data not loaded / scene not
    // in the table) falls back to the magnitude gate so behaviour never regresses.
    // -------------------------------------------------------------------------
    private const int SceneKindField     = 1; // world/town/field
    private const int SceneKindInstanced = 2; // instanced dungeon/raid

    // The real Mistveil Hunting Ground per-instance uuids the owner hit — BELOW 2^53.
    private const long MistveilBelowFloorUuid1 = 6220542868717568L; // 6.2e15
    private const long MistveilBelowFloorUuid2 = 5094642961874944L; // 5.1e15

    [Theory]
    [InlineData(MistveilBelowFloorUuid1)]
    [InlineData(MistveilBelowFloorUuid2)]
    public void Resolve_InstancedByKind_BelowFloor_ReturnsUuid(long sceneUuid)
        // THE FIX: a below-floor uuid on an instanced (kind 2) scene is still the run id.
        => Assert.Equal(sceneUuid, DungeonRunIdGate.Resolve(sceneUuid, SceneKindInstanced));

    [Fact]
    public void Resolve_InstancedByKind_AboveFloor_ReturnsUuid()
        => Assert.Equal(DungeonInstanceUuid, DungeonRunIdGate.Resolve(DungeonInstanceUuid, SceneKindInstanced));

    [Theory]
    [InlineData(OpenWorldFieldUuid)]
    [InlineData(TownUuid)]
    [InlineData(MistveilBelowFloorUuid1)]                              // even a large uuid, if the scene is field, is not a run
    [InlineData(DungeonRunIdGate.DungeonInstanceUuidFloor + 1)]        // fixes the "field scene exceeds floor" failure mode too
    public void Resolve_FieldByKind_AlwaysZero(long sceneUuid)
        => Assert.Equal(0L, DungeonRunIdGate.Resolve(sceneUuid, SceneKindField));

    [Fact]
    public void Resolve_UnknownKind_FallsBackToMagnitudeGate()
    {
        // null kind = game-data not loaded / scene missing from table → magnitude gate, unchanged.
        Assert.Equal(0L, DungeonRunIdGate.Resolve(MistveilBelowFloorUuid1, null));
        Assert.Equal(DungeonInstanceUuid, DungeonRunIdGate.Resolve(DungeonInstanceUuid, null));
    }

    [Fact]
    public void Resolve_KnownNonInstancedNonField_IsZero()
        // Any known kind that is not "instanced" (e.g. 0 = login/memory) is not a run.
        => Assert.Equal(0L, DungeonRunIdGate.Resolve(DungeonInstanceUuid, 0));
}
