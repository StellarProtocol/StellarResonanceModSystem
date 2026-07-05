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
    // Rank of the source currently holding the run-timer latch (0 = empty slot;
    // otherwise the RunTimerSource numeric value, lower = better). Written only
    // under _runTimerLock; the value itself stays readable lock-free via
    // Interlocked.Read on _runTimerStartMs.
    private int _runTimerRank;
    private readonly object _runTimerLock = new();

    // Settlement is a multi-field struct, so it can't be published with a single
    // volatile/interlocked write without tearing. Guard it with a small lock —
    // writes fire at most once per run (the result screen) and reads are cheap,
    // so contention is negligible.
    private readonly object _settlementLock = new();
    private DungeonSettlementInfo? _lastSettlement;

    // Outcome/defeated are sticky latches, same lifecycle as _lastSettlement:
    // published lock-free via Interlocked/Volatile, cleared on a genuinely-new
    // run and on Reset(), preserved across the drop-to-0 town transition.
    private int _lastOutcome;
    private int _lastDefeated;

    public long CurrentRunId => Interlocked.Read(ref _currentRunId);

    public DungeonSettlementInfo? LastSettlement
    {
        get { lock (_settlementLock) return _lastSettlement; }
    }

    public int CurrentDifficulty => Interlocked.CompareExchange(ref _currentDifficulty, 0, 0);

    public long RunTimerStartMs => Interlocked.Read(ref _runTimerStartMs);

    public Stellar.Abstractions.Domain.DungeonOutcome LastOutcome
        => (Stellar.Abstractions.Domain.DungeonOutcome)Volatile.Read(ref _lastOutcome);

    public int LastDefeatedCount => Volatile.Read(ref _lastDefeated);

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
            ClearRunTimerLatch();
            Interlocked.Exchange(ref _lastOutcome, 0);
            Interlocked.Exchange(ref _lastDefeated, 0);
        }
    }

    public void SetSettlement(int passTimeSeconds, int masterModeScore)
    {
        lock (_settlementLock)
            _lastSettlement = new DungeonSettlementInfo(passTimeSeconds, masterModeScore);
    }

    public void SetDifficulty(int difficulty)
        => Interlocked.Exchange(ref _currentDifficulty, difficulty);

    public void SetOutcome(int flowResult)
    {
        if (flowResult <= 0) return; // 0 = None = "not resolved"; ignore
        Interlocked.Exchange(ref _lastOutcome, flowResult);
    }

    public void SetDefeated(int count)
    {
        if (count < 0) return;
        Interlocked.Exchange(ref _lastDefeated, count);
    }

    public RunTimerWrite SetRunTimerStart(long startMs, RunTimerSource source)
    {
        // Zero is the wire's "run not started yet" value (timer_info.start_time /
        // flow_info.play_time are 0 on hub/pre-start deliveries) — a zero must
        // never overwrite a latched non-zero start. Non-zero writes compete by
        // SOURCE RANK (the RunTimerSource numeric value, lower = better): a
        // write wins when the slot is empty OR its rank is STRICTLY better than
        // the latched one, so the precise method-55 arrival edge (rank 1) can
        // UPGRADE the approximate flow.active_time entry-sync latch (rank 4)
        // that lands first on the live path. Equal/worse ranks are ignored —
        // duplicate method-55 ntfs (one per party member) cannot shift the
        // clock. Clearing happens exclusively via SetCurrentRun (new run id) or
        // Reset (logout).
        if (startMs == 0) return RunTimerWrite.Ignored;
        int rank = (int)source;
        lock (_runTimerLock)
        {
            int latchedRank = _runTimerRank;
            if (latchedRank != 0 && rank >= latchedRank) return RunTimerWrite.Ignored;
            Interlocked.Exchange(ref _runTimerStartMs, startMs);
            _runTimerRank = rank;
            return latchedRank == 0 ? RunTimerWrite.Latched : RunTimerWrite.Upgraded;
        }
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _currentRunId, 0);
        Interlocked.Exchange(ref _currentDifficulty, 0);
        ClearRunTimerLatch();
        lock (_settlementLock) _lastSettlement = null;
        Interlocked.Exchange(ref _lastOutcome, 0);
        Interlocked.Exchange(ref _lastDefeated, 0);
    }

    // Empty the run-timer latch slot (value + source rank together, under the
    // latch lock so a concurrent SetRunTimerStart can't see a half-cleared slot).
    private void ClearRunTimerLatch()
    {
        lock (_runTimerLock)
        {
            Interlocked.Exchange(ref _runTimerStartMs, 0);
            _runTimerRank = 0;
        }
    }
}
