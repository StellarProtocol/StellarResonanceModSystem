using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Implementation of <see cref="IDungeonState"/> (read side) and
/// <see cref="IDungeonStateSink"/> (write side). The Infrastructure combat probe
/// pushes the decoded dungeon <c>scene_uuid</c> on enter-scene (magnitude-gated
/// to dungeon instances), and the dungeon probe pushes the settlement — both on
/// the network receive thread; plugins read <see cref="CurrentRunId"/> /
/// <see cref="LastSettlement"/> on the main thread. State is published via
/// volatile/interlocked so reads are lock-free and never tear.
/// </summary>
internal sealed class DungeonStateService : IDungeonState, IDungeonStateSink
{
    private long _currentRunId;
    private int _currentDifficulty;
    private long _runTimerStartMs;

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

    public int CurrentDifficulty => Interlocked.CompareExchange(ref _currentDifficulty, 0, 0);

    public long RunTimerStartMs => Interlocked.Read(ref _runTimerStartMs);

    public void SetCurrentRun(long sceneUuid)
    {
        long previous = Interlocked.Exchange(ref _currentRunId, sceneUuid);
        // Clear the prior run's settlement only when a genuinely different run BEGINS
        // (a new non-zero id). Re-entering the same dungeon (same uuid) is idempotent —
        // keep settlement. Transitioning to 0 (leaving a dungeon to town/open-world)
        // must ALSO keep it: the upload plugin reads LastSettlement at archive time on
        // that very dungeon->town transition, so the just-earned clear/result must
        // survive the drop-to-0. The stale settlement is then cleared when the next
        // real run latches its id, or on Reset (logout).
        if (sceneUuid != 0 && previous != sceneUuid)
        {
            lock (_settlementLock) _lastSettlement = null;
            Interlocked.Exchange(ref _currentDifficulty, 0);
            Interlocked.Exchange(ref _runTimerStartMs, 0);
        }
    }

    public void SetSettlement(int passTimeSeconds, int masterModeScore)
    {
        lock (_settlementLock)
            _lastSettlement = new DungeonSettlementInfo(passTimeSeconds, masterModeScore);
    }

    public void SetDifficulty(int difficulty)
        => Interlocked.Exchange(ref _currentDifficulty, difficulty);

    public void SetRunTimerStart(long startMs)
    {
        // Zero is the wire's "run not started yet" value (flow_info.play_time is
        // 0 on hub/pre-start deliveries) — a zero must never overwrite a latched
        // non-zero start. Clearing happens exclusively via SetCurrentRun (new
        // run id) or Reset (logout).
        if (startMs == 0) return;
        Interlocked.Exchange(ref _runTimerStartMs, startMs);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _currentRunId, 0);
        Interlocked.Exchange(ref _currentDifficulty, 0);
        Interlocked.Exchange(ref _runTimerStartMs, 0);
        lock (_settlementLock) _lastSettlement = null;
    }
}
