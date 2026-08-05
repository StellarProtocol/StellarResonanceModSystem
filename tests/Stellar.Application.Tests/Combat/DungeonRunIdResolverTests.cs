using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// Covers the two-signal coordination that fixes ranked content ("No run id"): the
/// wire delivers the per-instance <c>AttrSceneUuid</c> (which can be below the 2^53
/// floor), and the game's OnEnterScene delivers the scene id (<c>IClientState.CurrentSceneName</c>)
/// a moment LATER — so the resolver re-resolves the run id on BOTH signals, classifying
/// via <c>IGameDataWorld.GetScene(id).SceneKind</c> and falling back to the magnitude
/// gate only when the scene kind is unknown.
/// </summary>
public sealed class DungeonRunIdResolverTests
{
    private const int MistveilSceneId = 6541;          // ranked instanced content (SceneType 2)
    private const int FieldSceneId    = 7;             // Asteria Plains — world/field (SceneType 1)
    private const long BelowFloorUuid = 6220542868717568L; // real Mistveil per-instance uuid, < 2^53
    private const long AboveFloorUuid = 493733355695636480L;

    // Minimal IGameDataWorld — only GetScene is exercised; the rest return null.
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
        var resolver = new DungeonRunIdResolver(sink, data, client);
        return (resolver, client, sink, data);
    }

    [Fact]
    public void InstancedScene_BelowFloorUuid_SinkGetsUuid()
    {
        // THE FIX: wire uuid arrives first (scene id still stale), then the game enters the
        // instanced scene — the run id must become the below-floor uuid, not stay 0.
        var (resolver, client, sink, data) = NewSut();
        data.SetScene(MistveilSceneId, DungeonRunIdGate.SceneKindInstanced);

        resolver.OnWireSceneUuid(BelowFloorUuid);           // wire first: scene id unknown -> magnitude -> 0 (provisional)
        client.RaiseSceneChanged(MistveilSceneId.ToString()); // scene id lands -> classify instanced -> uuid

        Assert.Equal(BelowFloorUuid, sink.CurrentRunId);
    }

    [Fact]
    public void FieldScene_SinkGetsZero()
    {
        var (resolver, client, sink, data) = NewSut();
        data.SetScene(FieldSceneId, 1); // world/field

        resolver.OnWireSceneUuid(AboveFloorUuid);          // even an above-floor uuid...
        client.RaiseSceneChanged(FieldSceneId.ToString());  // ...on a field scene is not a run

        Assert.Equal(0L, sink.CurrentRunId);
    }

    [Fact]
    public void SceneChangeBeforeWire_OrderIndependent()
    {
        // If the scene id lands before the wire uuid, the final state is still correct.
        var (resolver, client, sink, data) = NewSut();
        data.SetScene(MistveilSceneId, DungeonRunIdGate.SceneKindInstanced);

        client.RaiseSceneChanged(MistveilSceneId.ToString()); // scene id first (no uuid yet -> 0)
        resolver.OnWireSceneUuid(BelowFloorUuid);            // uuid lands -> uuid

        Assert.Equal(BelowFloorUuid, sink.CurrentRunId);
    }

    [Fact]
    public void UnknownScene_FallsBackToMagnitudeGate()
    {
        var (resolver, client, sink, _) = NewSut();
        // No SetScene → GetScene returns null → magnitude fallback.
        client.RaiseSceneChanged("99999");        // unknown scene id
        resolver.OnWireSceneUuid(BelowFloorUuid); // below floor + unknown -> 0
        Assert.Equal(0L, sink.CurrentRunId);

        resolver.OnWireSceneUuid(AboveFloorUuid); // above floor + unknown -> uuid
        Assert.Equal(AboveFloorUuid, sink.CurrentRunId);
    }
}
