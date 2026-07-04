using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// Exercises the direct run-id latch. The real-game bug the original method-23
/// "confirm" approach tried to fix turned out to be unfixable that way: the
/// method-23 (<c>SyncDungeonData</c>) packet ALSO fires in town, so confirming
/// on it promoted the town scene id. The fix is a DIRECT, magnitude-gated set:
/// the combat probe only calls <see cref="IDungeonStateSink.SetCurrentRun"/> for
/// dungeon-instance scene uuids (snowflakes &gt; 2^53), so the small town id is
/// never passed in. These tests cover the SERVICE contract — the magnitude gate
/// itself lives in the probe (<c>PandaCombatStubProbe.OnEnterScene</c>), so the
/// service treats every <c>SetCurrentRun</c> as authoritative.
/// </summary>
public sealed class DungeonStateServiceTests
{
    private const long DungeonId = 148061897948659712L;
    private const long Dungeon2Id = 148061897948659713L;

    private static (IDungeonState read, IDungeonStateSink write) NewService()
    {
        var svc = new DungeonStateService();
        return (svc, svc);
    }

    [Fact]
    public void SetCurrentRun_SetsRunIdDirectly()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        Assert.Equal(DungeonId, read.CurrentRunId);
    }

    [Fact]
    public void SetCurrentRun_SameId_KeepsSettlement()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetSettlement(120, 42);
        // Re-entering the same dungeon (same uuid) must not wipe the settlement.
        write.SetCurrentRun(DungeonId);
        Assert.Equal(DungeonId, read.CurrentRunId);
        Assert.NotNull(read.LastSettlement);
        Assert.Equal(120, read.LastSettlement!.Value.PassTimeSeconds);
    }

    [Fact]
    public void SetCurrentRun_NewId_ClearsPriorSettlement()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetSettlement(120, 42);

        // Player enters a second dungeon: the new enter-scene sets the new id and
        // the prior run's settlement is cleared.
        write.SetCurrentRun(Dungeon2Id);
        Assert.Equal(Dungeon2Id, read.CurrentRunId);
        Assert.Null(read.LastSettlement);
    }

    [Fact]
    public void SetCurrentRun_Zero_PreservesSettlement()
    {
        // Run-identity collision fix: leaving a dungeon to town/open-world clears the
        // run id to 0 (so the id can't linger onto a later open-world run), but the
        // just-earned settlement must SURVIVE — the upload plugin reads LastSettlement
        // at archive time on that very dungeon->town transition.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetSettlement(120, 42);

        write.SetCurrentRun(0);
        Assert.Equal(0L, read.CurrentRunId);
        Assert.NotNull(read.LastSettlement);
        Assert.Equal(120, read.LastSettlement!.Value.PassTimeSeconds);
        Assert.Equal(42, read.LastSettlement!.Value.MasterModeScore);
    }

    [Fact]
    public void SetCurrentRun_ZeroThenNewDungeon_ClearsStaleSettlement()
    {
        // After the settlement survives the drop-to-0 (above), the NEXT real run must
        // still clear the prior run's stale settlement when it latches its own id.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetSettlement(120, 42);
        write.SetCurrentRun(0);              // leave to town — settlement kept
        write.SetCurrentRun(Dungeon2Id);     // enter a new dungeon — stale settlement cleared

        Assert.Equal(Dungeon2Id, read.CurrentRunId);
        Assert.Null(read.LastSettlement);
    }

    [Fact]
    public void SettlementOnly_DoesNotChangeRunId()
    {
        // The dungeon probe (method-23) no longer touches the run id; it only
        // records the settlement against whatever run is current.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetSettlement(120, 42);
        Assert.Equal(DungeonId, read.CurrentRunId);
        Assert.NotNull(read.LastSettlement);
    }

    [Fact]
    public void SetDifficulty_PublishesRawValue()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetDifficulty(6);
        Assert.Equal(6, read.CurrentDifficulty);
    }

    [Fact]
    public void SetCurrentRun_NewId_ClearsPriorDifficulty()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetDifficulty(6);

        write.SetCurrentRun(Dungeon2Id);
        Assert.Equal(0, read.CurrentDifficulty);
    }

    [Fact]
    public void SetCurrentRun_Zero_PreservesDifficulty()
    {
        // Mirrors settlement: leaving to town clears the run id but the upload
        // plugin still needs the difficulty at archive time on that transition.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetDifficulty(6);

        write.SetCurrentRun(0);
        Assert.Equal(6, read.CurrentDifficulty);
    }

    [Fact]
    public void Reset_ClearsDifficulty()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetDifficulty(6);

        write.Reset();
        Assert.Equal(0, read.CurrentDifficulty);
    }

    private const long RunTimerStartMs = 1700000000000L;

    [Theory]
    [InlineData((int)RunTimerSource.Method55Edge)]
    [InlineData((int)RunTimerSource.TimerInfo)]
    [InlineData((int)RunTimerSource.FlowPlayTime)]
    [InlineData((int)RunTimerSource.FlowActiveTime)]
    public void SetRunTimerStart_EmptySlot_LatchesFromAnySource(int sourceRank)
    {
        // Any source may perform the initial latch when the slot is empty —
        // the rank only matters when competing against an existing latch.
        // (int-typed theory data: RunTimerSource is internal, and a public
        // xunit method cannot expose an internal parameter type.)
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        var result = write.SetRunTimerStart(RunTimerStartMs, (RunTimerSource)sourceRank);
        Assert.Equal(RunTimerWrite.Latched, result);
        Assert.Equal(RunTimerStartMs, read.RunTimerStartMs);
    }

    [Fact]
    public void SetRunTimerStart_HigherRank_UpgradesLowerRankLatch()
    {
        // THE live-path sequence: the entry sync latches the approximate
        // flow.active_time (rank 4) FIRST, then the method-55 play-start edge
        // (rank 1) arrives and must OVERWRITE it. A plain first-wins guard
        // would have frozen the approximation in.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs, RunTimerSource.FlowActiveTime);

        var result = write.SetRunTimerStart(RunTimerStartMs + 20000, RunTimerSource.Method55Edge);
        Assert.Equal(RunTimerWrite.Upgraded, result);
        Assert.Equal(RunTimerStartMs + 20000, read.RunTimerStartMs);
    }

    [Fact]
    public void SetRunTimerStart_LowerRank_DoesNotOverwriteHigherRankLatch()
    {
        // Once the precise edge is latched, a later approximate/payload source
        // must not shift the clock.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs, RunTimerSource.Method55Edge);

        var result = write.SetRunTimerStart(RunTimerStartMs + 5000, RunTimerSource.FlowActiveTime);
        Assert.Equal(RunTimerWrite.Ignored, result);
        Assert.Equal(RunTimerStartMs, read.RunTimerStartMs);
    }

    [Fact]
    public void SetRunTimerStart_EqualRank_DoesNotOverwrite()
    {
        // Method 55 fires once per party member (the game's own Lua impl
        // early-returns on self) — duplicate equal-rank writes are ignored.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs, RunTimerSource.Method55Edge);

        var result = write.SetRunTimerStart(RunTimerStartMs + 300, RunTimerSource.Method55Edge);
        Assert.Equal(RunTimerWrite.Ignored, result);
        Assert.Equal(RunTimerStartMs, read.RunTimerStartMs);
    }

    [Fact]
    public void SetRunTimerStart_IntermediateRank_UpgradesWorseButLosesToBetter()
    {
        // Full ladder: active_time (4) → timer_info (2) upgrades; then
        // play_time (3) must NOT downgrade; then method55 (1) upgrades again.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        Assert.Equal(RunTimerWrite.Latched,  write.SetRunTimerStart(1000, RunTimerSource.FlowActiveTime));
        Assert.Equal(RunTimerWrite.Upgraded, write.SetRunTimerStart(2000, RunTimerSource.TimerInfo));
        Assert.Equal(RunTimerWrite.Ignored,  write.SetRunTimerStart(3000, RunTimerSource.FlowPlayTime));
        Assert.Equal(RunTimerWrite.Upgraded, write.SetRunTimerStart(4000, RunTimerSource.Method55Edge));
        Assert.Equal(4000L, read.RunTimerStartMs);
    }

    [Fact]
    public void SetCurrentRun_NewId_ClearsPriorRunTimerStart()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs, RunTimerSource.TimerInfo);

        write.SetCurrentRun(Dungeon2Id);
        Assert.Equal(0L, read.RunTimerStartMs);
    }

    [Fact]
    public void SetCurrentRun_NewId_AlsoClearsLatchedRank()
    {
        // The rank must clear WITH the value: after a new run begins, even a
        // worse-ranked source must latch normally (empty-slot semantics).
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs, RunTimerSource.Method55Edge);

        write.SetCurrentRun(Dungeon2Id);
        var result = write.SetRunTimerStart(RunTimerStartMs + 9000, RunTimerSource.FlowActiveTime);
        Assert.Equal(RunTimerWrite.Latched, result);
        Assert.Equal(RunTimerStartMs + 9000, read.RunTimerStartMs);
    }

    [Fact]
    public void SetCurrentRun_Zero_PreservesRunTimerStart()
    {
        // Mirrors settlement/difficulty: leaving to town clears the run id but
        // the upload plugin still needs the timer start at archive time on that
        // transition.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs, RunTimerSource.TimerInfo);

        write.SetCurrentRun(0);
        Assert.Equal(RunTimerStartMs, read.RunTimerStartMs);
    }

    [Fact]
    public void SetRunTimerStart_Zero_DoesNotOverwriteLatchedValue()
    {
        // flow_info fields are 0 on hub/pre-start deliveries — a zero write
        // must never wipe a latched non-zero start, regardless of its rank.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs, RunTimerSource.FlowActiveTime);

        var result = write.SetRunTimerStart(0, RunTimerSource.Method55Edge);
        Assert.Equal(RunTimerWrite.Ignored, result);
        Assert.Equal(RunTimerStartMs, read.RunTimerStartMs);
    }

    [Fact]
    public void SetRunTimerStart_NewRun_RelatchesAfterClear()
    {
        // The rank guard is per-run: a new run id clears the latch, and the
        // next run's first non-zero write must latch normally.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs, RunTimerSource.TimerInfo);

        write.SetCurrentRun(Dungeon2Id);
        Assert.Equal(0L, read.RunTimerStartMs);

        write.SetRunTimerStart(RunTimerStartMs + 7000, RunTimerSource.TimerInfo);
        Assert.Equal(RunTimerStartMs + 7000, read.RunTimerStartMs);
    }

    [Fact]
    public void SetRunTimerStart_Zero_OnFreshRun_StaysZero()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        var result = write.SetRunTimerStart(0, RunTimerSource.TimerInfo);
        Assert.Equal(RunTimerWrite.Ignored, result);
        Assert.Equal(0L, read.RunTimerStartMs);
    }

    [Fact]
    public void Reset_ClearsRunTimerStartAndRank()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs, RunTimerSource.Method55Edge);

        write.Reset();
        Assert.Equal(0L, read.RunTimerStartMs);

        // Rank cleared too: a worse-ranked source can latch the fresh slot.
        var result = write.SetRunTimerStart(RunTimerStartMs + 1000, RunTimerSource.FlowActiveTime);
        Assert.Equal(RunTimerWrite.Latched, result);
        Assert.Equal(RunTimerStartMs + 1000, read.RunTimerStartMs);
    }

    [Fact]
    public void Reset_ClearsRunIdAndSettlement()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetSettlement(120, 42);

        write.Reset();
        Assert.Equal(0L, read.CurrentRunId);
        Assert.Null(read.LastSettlement);
    }
}
