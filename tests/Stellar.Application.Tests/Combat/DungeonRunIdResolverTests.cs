using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// Covers the run-id resolver. The wire enter-scene carries BOTH the per-instance uuid
/// (<c>AttrSceneUuid</c> 342) AND the scene TEMPLATE id (<c>AttrSceneBasicId</c> 341), so the
/// resolver classifies the run at zone-in — EARLY, before the game's later OnEnterScene and before
/// the first dungeon flow delivery, so the dungeon-state clear fires ahead of the flow (no mid-run
/// flow-version reset). <c>IClientState.SceneChanged</c> is a fallback for when the wire basic id is
/// absent or the scene table wasn't loaded yet at zone-in.
/// </summary>
public sealed class DungeonRunIdResolverTests
{
    private const int MistveilBasicId = 6541;          // ranked instanced content (SceneType 2)
    private const int TownBasicId     = 8;             // Asterleeds — city (SceneType 1)
    private const int FieldBasicId    = 7;             // Asteria Plains — world/field (SceneType 1)
    private const long BelowFloorUuid = 6220542868717568L; // real Mistveil per-instance uuid, < 2^53
    private const long AboveFloorUuid = 493733355695636480L;

    private sealed class FakeGameDataWorld : IGameDataWorld
    {
        private readonly System.Collections.Generic.Dictionary<int, int> _kinds = new();
        public void SetScene(int id, int sceneKind) => _kinds[id] = sceneKind;
        public SceneInfo? GetScene(int id)
            => _kinds.TryGetValue(id, out var kind) ? new SceneInfo(id, "", 0, kind) : (SceneInfo?)null;
        public MonsterInfo? GetMonster(int id) => null;
        public MonsterInfo? GetMonsterByEntity(EntityId entityId) => null;
        public NpcInfo? GetNpc(int id) => null;
        public MapInfo? GetMap(int id) => null;
    }

    private static (DungeonRunIdResolver resolver, StubClientState client, DungeonStateService sink, FakeGameDataWorld data) NewSut()
    {
        var client = new StubClientState();
        var sink = new DungeonStateService();
        var data = new FakeGameDataWorld();
        data.SetScene(MistveilBasicId, DungeonRunIdGate.SceneKindInstanced);
        data.SetScene(TownBasicId, 1);
        data.SetScene(FieldBasicId, 1);
        var resolver = new DungeonRunIdResolver(sink, data, client);
        return (resolver, client, sink, data);
    }

    [Fact]
    public void InstancedScene_ClassifiedEarlyFromWireBasicId_BelowFloorUuid()
    {
        // THE FIX: the wire carries the scene basic id, so the run-id is the uuid IMMEDIATELY at the
        // wire enter-scene — no SceneChanged needed, so InstancedRun is true from the very first tick.
        var (resolver, _, sink, _) = NewSut();
        resolver.OnWireEnterScene(BelowFloorUuid, MistveilBasicId);
        Assert.Equal(BelowFloorUuid, sink.CurrentRunId);
    }

    [Fact]
    public void FieldScene_FromWireBasicId_IsZero()
    {
        var (resolver, _, sink, _) = NewSut();
        resolver.OnWireEnterScene(AboveFloorUuid, FieldBasicId);   // even above-floor: field is not a run
        Assert.Equal(0L, sink.CurrentRunId);
    }

    [Fact]
    public void UnknownBasicId_FallsBackToSceneNameThenMagnitude()
    {
        var (resolver, client, sink, _) = NewSut();
        // basic id 0 (absent) + no scene name yet -> magnitude fallback.
        resolver.OnWireEnterScene(BelowFloorUuid, 0);
        Assert.Equal(0L, sink.CurrentRunId);                       // below floor + unknown -> 0
        // …then the game publishes the scene id -> re-resolves via CurrentSceneName (instanced).
        client.RaiseSceneChanged(MistveilBasicId.ToString());
        Assert.Equal(BelowFloorUuid, sink.CurrentRunId);
    }

    [Fact]
    public void UnknownBasicId_AboveFloor_MagnitudeFallbackKeepsUuid()
    {
        var (resolver, _, sink, _) = NewSut();
        resolver.OnWireEnterScene(AboveFloorUuid, 0);              // unknown scene -> magnitude gate
        Assert.Equal(AboveFloorUuid, sink.CurrentRunId);
    }

    [Fact]
    public void ReResolveIsIdempotent_NoSpuriousChangeOnSceneChanged()
    {
        // Wire classified instanced -> uuid. The later game OnEnterScene must not change it (same value),
        // so it never re-triggers the dungeon-state "new run" clear mid-run.
        var (resolver, client, sink, _) = NewSut();
        resolver.OnWireEnterScene(BelowFloorUuid, MistveilBasicId);
        Assert.Equal(BelowFloorUuid, sink.CurrentRunId);
        client.RaiseSceneChanged(MistveilBasicId.ToString());
        Assert.Equal(BelowFloorUuid, sink.CurrentRunId);           // unchanged
    }
}
