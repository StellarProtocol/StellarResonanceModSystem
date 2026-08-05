using System;
using System.Globalization;
using System.Threading;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Resolves the dungeon run id from TWO signals that arrive at different times and on
/// different threads, then writes it to <see cref="IDungeonStateSink.SetCurrentRun"/>:
/// <list type="bullet">
/// <item>the per-instance <c>AttrSceneUuid</c> latched from the enter-scene WIRE packet
/// (<see cref="OnWireSceneUuid"/>, network receive thread) — the run-id VALUE; and</item>
/// <item>the scene id the GAME publishes via <c>OnEnterScene</c>
/// (<see cref="IClientState.CurrentSceneName"/> / <see cref="IClientState.SceneChanged"/>,
/// main thread) — which classifies the scene through <c>IGameDataWorld.GetScene(id).SceneKind</c>.</item>
/// </list>
/// The wire uuid arrives FIRST (the scene id is still the previous scene at that instant),
/// so the run id is re-resolved on BOTH signals and the settled value is order-independent.
/// Classification (<see cref="DungeonRunIdGate"/>) beats the magnitude heuristic when the
/// scene kind is known — the fix for ranked content whose per-instance uuid falls below the
/// 2^53 floor (e.g. Mistveil Hunting Ground); the magnitude gate remains only as the fallback
/// for scenes not yet in the loaded table.
/// </summary>
internal sealed class DungeonRunIdResolver
{
    private readonly IDungeonStateSink _sink;
    private readonly IGameDataWorld _gameData;
    private readonly IClientState _clientState;

    // The last per-instance scene uuid from the wire; read on both threads via Interlocked.
    private long _pendingSceneUuid;

    public DungeonRunIdResolver(IDungeonStateSink sink, IGameDataWorld gameData, IClientState clientState)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
        _clientState.SceneChanged += OnSceneChanged;
    }

    /// <summary>Latch the enter-scene wire uuid and re-resolve (network receive thread).</summary>
    public void OnWireSceneUuid(long sceneUuid)
    {
        Interlocked.Exchange(ref _pendingSceneUuid, sceneUuid);
        Reresolve();
    }

    // The game entered/left a scene: CurrentSceneName is now current — re-classify (main thread).
    private void OnSceneChanged(string? sceneName) => Reresolve();

    // Recompute CurrentRunId from the latest wire uuid + the active scene's kind. Idempotent, so
    // whichever of the two signals arrives last produces the settled run id.
    private void Reresolve()
        => _sink.SetCurrentRun(DungeonRunIdGate.Resolve(Interlocked.Read(ref _pendingSceneUuid), CurrentSceneKind()));

    // The active scene's SceneTable SceneType, or null when CurrentSceneName isn't a numeric scene
    // id or the scene isn't in the loaded table — null makes the gate fall back to its magnitude test.
    private int? CurrentSceneKind()
        => int.TryParse(_clientState.CurrentSceneName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? _gameData.GetScene(id)?.SceneKind
            : null;
}
