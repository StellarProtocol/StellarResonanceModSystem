using System;
using System.Globalization;
using System.Threading;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Resolves the dungeon run id from the enter-scene WIRE packet and writes it to
/// <see cref="IDungeonStateSink.SetCurrentRun"/>. The packet carries BOTH the per-instance
/// <c>AttrSceneUuid</c> (342 — the run-id VALUE) and the scene TEMPLATE id <c>AttrSceneBasicId</c>
/// (341), so the scene is classified through the game scene table
/// (<c>IGameDataWorld.GetScene(basicId).SceneKind</c>) at zone-in — an instanced scene keeps its uuid
/// as the run id even below the 2^53 magnitude floor (the 3.7 "No run id" fix), town/field resolve to
/// 0 (the run-identity collision fix). Doing this EARLY — before the game's own OnEnterScene and
/// before the first dungeon flow delivery — is what keeps the dungeon-state "new run" clear ahead of
/// the flow, so the flow-state version is never reset mid-run (which otherwise breaks the archive
/// engine's stage/boss-segment tracking). <see cref="IClientState.SceneChanged"/> is a FALLBACK: it
/// re-resolves when the wire carried no basic id, or the scene table had not loaded yet at zone-in.
/// The magnitude gate remains the last-resort fallback when the scene kind is unknown at both.
/// </summary>
internal sealed class DungeonRunIdResolver
{
    private readonly IDungeonStateSink _sink;
    private readonly IGameDataWorld _gameData;
    private readonly IClientState _clientState;

    // Latest wire enter-scene identities; read on both the network-receive and main threads.
    private long _pendingSceneUuid;
    private int _pendingSceneBasicId;

    public DungeonRunIdResolver(IDungeonStateSink sink, IGameDataWorld gameData, IClientState clientState)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _clientState.SceneChanged += OnSceneChanged;
    }

    /// <summary>Latch the enter-scene wire identities and resolve the run id (network receive thread).</summary>
    public void OnWireEnterScene(long sceneUuid, int sceneBasicId)
    {
        Interlocked.Exchange(ref _pendingSceneUuid, sceneUuid);
        Interlocked.Exchange(ref _pendingSceneBasicId, sceneBasicId);
        Reresolve();
    }

    // Fallback re-resolve when the game publishes the scene id (main thread) — only changes anything
    // if the wire basic id was absent/unclassifiable at zone-in; otherwise it re-computes the same value.
    private void OnSceneChanged(string? sceneName) => Reresolve();

    private void Reresolve()
        => _sink.SetCurrentRun(DungeonRunIdGate.Resolve(Interlocked.Read(ref _pendingSceneUuid), CurrentSceneKind()));

    // The active scene's SceneTable SceneType: prefer the wire basic id (authoritative, available at
    // zone-in); fall back to the game-published CurrentSceneName; null (→ magnitude gate) when neither
    // resolves to a scene in the loaded table.
    private int? CurrentSceneKind()
    {
        int basicId = Volatile.Read(ref _pendingSceneBasicId);
        if (basicId != 0 && _gameData.GetScene(basicId) is { } byBasic) return byBasic.SceneKind;
        if (int.TryParse(_clientState.CurrentSceneName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nameId)
            && _gameData.GetScene(nameId) is { } byName) return byName.SceneKind;
        return null;
    }
}
