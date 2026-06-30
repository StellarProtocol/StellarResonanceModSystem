using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// Exercises the pending → confirm run-id latch. The real-game bug: the player
/// enters a dungeon, clears it, and RETURNS TO TOWN before the upload plugin
/// archives — so a direct enter-scene → CurrentRunId write let the town scene id
/// clobber the dungeon run id. The latch fixes that: enter-scene only sets a
/// pending id; only a dungeon-only SyncDungeonData (ConfirmDungeonRun) promotes it.
/// </summary>
public sealed class DungeonStateServiceTests
{
    private const long DungeonId = 148061897948659712L;
    private const long TownId    = 281509336449024L;
    private const long Dungeon2Id = 148061897948659713L;

    private static (IDungeonState read, IDungeonStateSink write) NewService()
    {
        var svc = new DungeonStateService();
        return (svc, svc);
    }

    [Fact]
    public void SetPendingScene_DoesNotChangeCurrentRunId()
    {
        var (read, write) = NewService();
        write.SetPendingScene(DungeonId);
        Assert.Equal(0L, read.CurrentRunId);
    }

    [Fact]
    public void ConfirmDungeonRun_PromotesPendingToCurrent()
    {
        var (read, write) = NewService();
        write.SetPendingScene(DungeonId);
        write.ConfirmDungeonRun();
        Assert.Equal(DungeonId, read.CurrentRunId);
    }

    [Fact]
    public void ReturnToTown_AfterConfirm_DoesNotClobberRunId()
    {
        // The exact in-game sequence: enter dungeon → SyncDungeonData confirms →
        // clear → enter town (pending=town, but NO SyncDungeonData in town).
        var (read, write) = NewService();
        write.SetPendingScene(DungeonId);
        write.ConfirmDungeonRun();
        Assert.Equal(DungeonId, read.CurrentRunId);

        write.SetPendingScene(TownId); // returned to town
        Assert.Equal(DungeonId, read.CurrentRunId); // run id survives — no confirm in town
    }

    [Fact]
    public void ConfirmWithZeroPending_IsNoOp()
    {
        var (read, write) = NewService();
        // No enter-scene yet (pending uninitialised). A stray method-23 must not
        // promote 0 into the run id.
        write.ConfirmDungeonRun();
        Assert.Equal(0L, read.CurrentRunId);
    }

    [Fact]
    public void RepeatedConfirm_IsIdempotent_AndKeepsSettlement()
    {
        var (read, write) = NewService();
        write.SetPendingScene(DungeonId);
        write.ConfirmDungeonRun();
        write.SetSettlement(120, 42);
        // A repeat SyncDungeonData for the same dungeon must not wipe the settlement.
        write.ConfirmDungeonRun();
        Assert.Equal(DungeonId, read.CurrentRunId);
        Assert.NotNull(read.LastSettlement);
        Assert.Equal(120, read.LastSettlement!.Value.PassTimeSeconds);
    }

    [Fact]
    public void SecondDungeon_PromotesNewIdAndClearsPriorSettlement()
    {
        var (read, write) = NewService();
        write.SetPendingScene(DungeonId);
        write.ConfirmDungeonRun();
        write.SetSettlement(120, 42);

        // Player enters a second dungeon: new enter-scene latches pending, its
        // method-23 promotes it — and the prior run's settlement is cleared.
        write.SetPendingScene(Dungeon2Id);
        write.ConfirmDungeonRun();
        Assert.Equal(Dungeon2Id, read.CurrentRunId);
        Assert.Null(read.LastSettlement);
    }

    [Fact]
    public void Reset_ClearsBothPendingAndCurrent()
    {
        var (read, write) = NewService();
        write.SetPendingScene(DungeonId);
        write.ConfirmDungeonRun();
        write.SetSettlement(120, 42);

        write.Reset();
        Assert.Equal(0L, read.CurrentRunId);
        Assert.Null(read.LastSettlement);

        // Pending was cleared too: a confirm after reset (with no new enter-scene)
        // must not resurrect the dungeon id.
        write.ConfirmDungeonRun();
        Assert.Equal(0L, read.CurrentRunId);
    }
}
