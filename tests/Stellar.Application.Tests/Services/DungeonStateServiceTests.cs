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

    [Fact]
    public void SetRunTimerStart_PublishesRawValue()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs);
        Assert.Equal(RunTimerStartMs, read.RunTimerStartMs);
    }

    [Fact]
    public void SetCurrentRun_NewId_ClearsPriorRunTimerStart()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs);

        write.SetCurrentRun(Dungeon2Id);
        Assert.Equal(0L, read.RunTimerStartMs);
    }

    [Fact]
    public void SetCurrentRun_Zero_PreservesRunTimerStart()
    {
        // Mirrors settlement/difficulty: leaving to town clears the run id but
        // the upload plugin still needs the timer start at archive time on that
        // transition.
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs);

        write.SetCurrentRun(0);
        Assert.Equal(RunTimerStartMs, read.RunTimerStartMs);
    }

    [Fact]
    public void Reset_ClearsRunTimerStart()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetRunTimerStart(RunTimerStartMs);

        write.Reset();
        Assert.Equal(0L, read.RunTimerStartMs);
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
