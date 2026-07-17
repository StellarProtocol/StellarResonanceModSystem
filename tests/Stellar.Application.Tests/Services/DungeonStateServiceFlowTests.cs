using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// EDungeonState surfacing (Part B of the 2026-07-17 sync spec): CurrentFlowState latches the
/// raw wire value; FlowStateVersion bumps once per CHANGE (never on a same-value re-delivery)
/// and is the poll-friendly change notification. Cleared on a new run and on Reset.
/// </summary>
public sealed class DungeonStateServiceFlowTests
{
    private const long DungeonId  = 148061897948659712L;
    private const long Dungeon2Id = 148061897948659713L;

    private static (IDungeonState read, IDungeonStateSink write) NewService()
    {
        var svc = new DungeonStateService();
        return (svc, svc);
    }

    [Fact]
    public void SetFlowState_LatchesValueAndBumpsVersion()
    {
        var (read, write) = NewService();
        write.SetFlowState(3);   // Playing
        Assert.Equal(DungeonFlowState.Playing, read.CurrentFlowState);
        Assert.Equal(1, read.FlowStateVersion);
    }

    [Fact]
    public void SameValueRedelivery_DoesNotBumpVersion()
    {
        var (read, write) = NewService();
        write.SetFlowState(3);
        write.SetFlowState(3);
        Assert.Equal(1, read.FlowStateVersion);
    }

    [Fact]
    public void EachTransition_BumpsVersionOnce()
    {
        var (read, write) = NewService();
        write.SetFlowState(2);   // Ready
        write.SetFlowState(3);   // Playing
        write.SetFlowState(4);   // End
        Assert.Equal(DungeonFlowState.End, read.CurrentFlowState);
        Assert.Equal(3, read.FlowStateVersion);
    }

    [Fact]
    public void NegativeValue_IsIgnored()
    {
        var (read, write) = NewService();
        write.SetFlowState(3);
        write.SetFlowState(-1);
        Assert.Equal(DungeonFlowState.Playing, read.CurrentFlowState);
        Assert.Equal(1, read.FlowStateVersion);
    }

    [Fact]
    public void UnknownFutureValue_SurfacesAsIs()
    {
        var (read, write) = NewService();
        write.SetFlowState(9);
        Assert.Equal((DungeonFlowState)9, read.CurrentFlowState);
    }

    [Fact]
    public void NewRun_ClearsFlowState()
    {
        var (read, write) = NewService();
        write.SetCurrentRun(DungeonId);
        write.SetFlowState(3);
        write.SetCurrentRun(Dungeon2Id);
        Assert.Equal(DungeonFlowState.None, read.CurrentFlowState);
        Assert.Equal(0, read.FlowStateVersion);
    }

    [Fact]
    public void Reset_ClearsFlowState()
    {
        var (read, write) = NewService();
        write.SetFlowState(5);
        write.Reset();
        Assert.Equal(DungeonFlowState.None, read.CurrentFlowState);
        Assert.Equal(0, read.FlowStateVersion);
    }
}
