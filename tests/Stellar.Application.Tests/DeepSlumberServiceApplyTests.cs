using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

public sealed class DeepSlumberServiceApplyTests
{
    private sealed class FakeRead : IDeepSlumberProbe
    {
        public DeepSlumberState? State;
        public bool IsResolved => true;
        public DeepSlumberState? Read() => State;
    }

    private sealed class FakeWrite : IDeepSlumberWriteProbe
    {
        public bool Resolved = true;
        public readonly List<string> Calls = new();
        public int EnableCode, ResetCode, ActivateCode, SocketCode, UnsocketCode;
        public System.Action? AfterCall;
        public bool IsResolved => Resolved;
        public Task<int> EnableLineAsync(int a, CancellationToken ct) { Calls.Add($"enable:{a}"); AfterCall?.Invoke(); return Task.FromResult(EnableCode); }
        public Task<int> ResetNodesAsync(int a, CancellationToken ct) { Calls.Add($"reset:{a}"); AfterCall?.Invoke(); return Task.FromResult(ResetCode); }
        public Task<int> ActivateNodeAsync(int n, CancellationToken ct) { Calls.Add($"activate:{n}"); AfterCall?.Invoke(); return Task.FromResult(ActivateCode); }
        public Task<int> SocketFactorAsync(int n, int i, CancellationToken ct) { Calls.Add($"socket:{n}:{i}"); AfterCall?.Invoke(); return Task.FromResult(SocketCode); }
        public Task<int> UnsocketFactorAsync(int n, int c, CancellationToken ct) { Calls.Add($"unsocket:{n}"); AfterCall?.Invoke(); return Task.FromResult(UnsocketCode); }
    }

    private static DeepSlumberState LiveActive(int areaId, params (int, int)[] mids)
    {
        var m = new List<int[]>();
        foreach (var (n, i) in mids) m.Add(new[] { n, i });
        var area = new DeepSlumberArea(areaId, true, 0, new List<int[]>(), m, new List<int[]>());
        return new DeepSlumberState(new List<int[]>(), new List<DeepSlumberLine> { new(3, 800522, new List<DeepSlumberArea> { area }) });
    }

    private static DeepSlumberSetup Target(int areaId, params (int, int)[] factors)
    {
        var f = new List<int[]>();
        foreach (var (n, i) in factors) f.Add(new[] { n, i });
        return new DeepSlumberSetup(1, new List<DeepSlumberAreaBinding> { new(areaId, f) });
    }

    private static DeepSlumberState LiveActiveTree(int areaId, int[] anchors, params (int, int)[] mids)
    {
        var m = new List<int[]>();
        foreach (var (n, i) in mids) m.Add(new[] { n, i });
        var normals = new List<int[]>();
        foreach (var n in anchors) normals.Add(new[] { n, 1 });
        var area = new DeepSlumberArea(areaId, true, 0, new List<int[]>(), m, normals);
        return new DeepSlumberState(new List<int[]>(), new List<DeepSlumberLine> { new(3, 800522, new List<DeepSlumberArea> { area }) });
    }

    private static DeepSlumberSetup TargetTree(int areaId, int[] anchors, params (int, int)[] factors)
    {
        var f = new List<int[]>();
        foreach (var (n, i) in factors) f.Add(new[] { n, i });
        return new DeepSlumberSetup(1, new List<DeepSlumberAreaBinding> { new(areaId, f) { NormalNodes = anchors } });
    }

    [Fact]
    public async Task TreeDiffers_DrivesResetThenActivateThenSocket_InPhaseOrder()
    {
        // Live tree {1001,1002} + a factor; target tree {1001,1003} + a factor. The service must drive
        // the ops in phase order: reset the area, activate every target anchor, then socket the factor —
        // and issue NO unsocket (the reset already returned the live factor to the bag).
        var read = new FakeRead { State = LiveActiveTree(5, new[] { 1001, 1002 }, (118, 111)) };
        var write = new FakeWrite();
        var svc = new DeepSlumberService(read, write);
        var result = await svc.ApplySetupAsync(TargetTree(5, new[] { 1001, 1003 }, (200, 999)));

        Assert.Equal(DeepSlumberApplyResult.Success, result);
        Assert.Equal(new[] { "reset:5", "activate:1001", "activate:1003", "socket:200:999" }, write.Calls);
        Assert.DoesNotContain(write.Calls, c => c.StartsWith("unsocket"));
    }

    [Fact]
    public async Task AlreadyMatched_IssuesNoCalls()
    {
        var read = new FakeRead { State = LiveActive(5, (118, 20020001)) };
        var write = new FakeWrite();
        var svc = new DeepSlumberService(read, write);
        Assert.Equal(DeepSlumberApplyResult.AlreadyMatched, await svc.ApplySetupAsync(Target(5, (118, 20020001))));
        Assert.Empty(write.Calls);
    }

    [Fact]
    public async Task AllOk_ReturnsSuccess()
    {
        var svc = new DeepSlumberService(new FakeRead { State = LiveActive(5) }, new FakeWrite());
        Assert.Equal(DeepSlumberApplyResult.Success, await svc.ApplySetupAsync(Target(5, (118, 222))));
    }

    [Fact]
    public async Task EnableRefused_NothingElseApplied_ReturnsRefused()
    {
        var read = new FakeRead { State = new DeepSlumberState(new List<int[]>(), new List<DeepSlumberLine>()) }; // area 5 inactive/absent
        var write = new FakeWrite { EnableCode = 7 };
        var svc = new DeepSlumberService(read, write);
        Assert.Equal(DeepSlumberApplyResult.Refused, await svc.ApplySetupAsync(Target(5)));
    }

    [Fact]
    public async Task FactorFailsAfterEnableOk_ReturnsPartialFailure()
    {
        var read = new FakeRead { State = new DeepSlumberState(new List<int[]>(), new List<DeepSlumberLine>()) };
        var write = new FakeWrite { EnableCode = 0, SocketCode = 9 };
        var svc = new DeepSlumberService(read, write);
        Assert.Equal(DeepSlumberApplyResult.PartialFailure, await svc.ApplySetupAsync(Target(5, (118, 222))));
    }

    [Fact]
    public async Task ProbeUnresolved_ReturnsUnavailable()
    {
        var svc = new DeepSlumberService(new FakeRead { State = LiveActive(5) }, new FakeWrite { Resolved = false });
        Assert.Equal(DeepSlumberApplyResult.Unavailable, await svc.ApplySetupAsync(Target(5)));
    }

    [Fact]
    public async Task AlreadyCancelled_ReturnsCancelled_IssuesNoCalls()
    {
        var read = new FakeRead { State = new DeepSlumberState(new List<int[]>(), new List<DeepSlumberLine>()) }; // area 5 inactive/absent → non-empty plan
        var write = new FakeWrite();
        var svc = new DeepSlumberService(read, write);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(DeepSlumberApplyResult.Cancelled, await svc.ApplySetupAsync(Target(5), cts.Token));
        Assert.Empty(write.Calls);
    }

    [Fact]
    public async Task CancelledAfterFirstOp_ReturnsPartialFailure()
    {
        var read = new FakeRead { State = new DeepSlumberState(new List<int[]>(), new List<DeepSlumberLine>()) };
        var write = new FakeWrite { EnableCode = 0, SocketCode = 0 };
        using var cts = new CancellationTokenSource();
        write.AfterCall = () => cts.Cancel();
        var svc = new DeepSlumberService(read, write);
        Assert.Equal(DeepSlumberApplyResult.PartialFailure, await svc.ApplySetupAsync(Target(5, (118, 222)), cts.Token));
        Assert.Single(write.Calls);
    }
}
