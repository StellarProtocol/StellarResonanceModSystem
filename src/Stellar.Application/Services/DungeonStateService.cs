using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Implementation of <see cref="IDungeonState"/> (read side) and
/// <see cref="IDungeonStateSink"/> (write side). The Infrastructure dungeon
/// probe pushes the decoded <c>scene_uuid</c> + settlement on the network
/// receive thread; plugins read <see cref="CurrentRunId"/> / <see cref="LastSettlement"/>
/// on the main thread. State is published via volatile/interlocked so reads are
/// lock-free and never tear.
/// </summary>
internal sealed class DungeonStateService : IDungeonState, IDungeonStateSink
{
    private long _currentRunId;

    // The most recent enter-scene uuid, latched but NOT yet promoted to the live
    // run id. Every enter-scene (dungeon or town) overwrites it; only a dungeon-only
    // SyncDungeonData packet promotes it via ConfirmDungeonRun. This keeps a clear →
    // return-to-town transition from clobbering the dungeon run id before upload.
    private long _pendingSceneId;

    // Settlement is a multi-field struct, so it can't be published with a single
    // volatile/interlocked write without tearing. Guard it with a small lock —
    // writes fire at most once per run (the result screen) and reads are cheap,
    // so contention is negligible.
    private readonly object _settlementLock = new();
    private DungeonSettlementInfo? _lastSettlement;

    public long CurrentRunId => Interlocked.Read(ref _currentRunId);

    public DungeonSettlementInfo? LastSettlement
    {
        get { lock (_settlementLock) return _lastSettlement; }
    }

    public void SetPendingScene(long sceneUuid)
    {
        // Latch only — never touches CurrentRunId. A town the player returns to
        // updates this but stays unpromoted (no SyncDungeonData follows in town).
        Interlocked.Exchange(ref _pendingSceneId, sceneUuid);
    }

    public void ConfirmDungeonRun()
    {
        long pending = Interlocked.Read(ref _pendingSceneId);
        // Guard: ignore the uninitialised case. Only a real (non-zero) pending
        // scene gets promoted.
        if (pending <= 0)
            return;

        long previous = Interlocked.Exchange(ref _currentRunId, pending);
        // A new run id means the prior run's settlement no longer applies.
        // Re-confirming the same dungeon (repeat SyncDungeonData) is idempotent.
        if (previous != pending)
            lock (_settlementLock) _lastSettlement = null;
    }

    public void SetSettlement(int passTimeSeconds, int masterModeScore)
    {
        lock (_settlementLock)
            _lastSettlement = new DungeonSettlementInfo(passTimeSeconds, masterModeScore);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _currentRunId, 0);
        Interlocked.Exchange(ref _pendingSceneId, 0);
        lock (_settlementLock) _lastSettlement = null;
    }
}
