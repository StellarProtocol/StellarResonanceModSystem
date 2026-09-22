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

    // Models the game's anchor dependency: ActiveNormalNode(n) returns 5126 until n's parent is active.
    private sealed class PrereqFakeWrite : IDeepSlumberWriteProbe
    {
        private readonly Dictionary<int, int> _parent;   // node -> required parent (0 = root/none)
        private readonly HashSet<int> _active = new();
        public readonly List<int> ActivateOrder = new();
        public PrereqFakeWrite(Dictionary<int, int> parent) => _parent = parent;
        public bool IsResolved => true;
        public Task<int> EnableLineAsync(int a, CancellationToken ct) => Task.FromResult(0);
        public Task<int> ResetNodesAsync(int a, CancellationToken ct) { _active.Clear(); return Task.FromResult(0); }
        public Task<int> ActivateNodeAsync(int n, CancellationToken ct)
        {
            var parent = _parent.TryGetValue(n, out var p) ? p : 0;
            if (parent != 0 && !_active.Contains(parent))
                return Task.FromResult(DeepSlumberWriteCode.PreTalentNodeNotActivated);
            _active.Add(n);
            ActivateOrder.Add(n);
            return Task.FromResult(0);
        }
        public Task<int> SocketFactorAsync(int n, int i, CancellationToken ct) => Task.FromResult(0);
        public Task<int> UnsocketFactorAsync(int n, int c, CancellationToken ct) => Task.FromResult(0);
    }

    [Fact]
    public async Task TreeAnchors_ActivatedOutOfDependencyOrder_ConvergeToSuccess()
    {
        // Anchors depend parent-first: 30 (root) <- 20 <- 10. The reconciler emits ActivateNode in
        // ASCENDING id order (10,20,30) — the wrong order — and the game returns 5126 for a node whose
        // parent isn't active yet. The Activate phase must requeue the 5126s until every anchor lands
        // (owner 2026-09-22: a class round-trip rebuilds the whole tree, never half-completes+locks it).
        var read = new FakeRead { State = LiveActiveTree(5, new[] { 999 }) };   // differs -> reset+rebuild
        var write = new PrereqFakeWrite(new Dictionary<int, int> { [10] = 20, [20] = 30, [30] = 0 });
        var svc = new DeepSlumberService(read, write);
        var result = await svc.ApplySetupAsync(TargetTree(5, new[] { 10, 20, 30 }));
        Assert.Equal(DeepSlumberApplyResult.Success, result);
        Assert.Equal(new[] { 30, 20, 10 }, write.ActivateOrder);   // self-ordered into dependency order
    }

    [Fact]
    public async Task TreeAnchor_WithUnsatisfiablePrereq_FailsWithoutHanging()
    {
        // Node 10 requires parent 40, which is NOT in the target set (a corrupt/partial binding). Once
        // 20 and 30 land, a pass makes no further progress on 10 — it is reported failed rather than
        // looping forever.
        var read = new FakeRead { State = LiveActiveTree(5, new[] { 999 }) };
        var write = new PrereqFakeWrite(new Dictionary<int, int> { [10] = 40, [20] = 0, [30] = 20 });
        var svc = new DeepSlumberService(read, write);
        var result = await svc.ApplySetupAsync(TargetTree(5, new[] { 10, 20, 30 }));
        Assert.Equal(DeepSlumberApplyResult.PartialFailure, result);  // 20,30 activated; 10 cannot
        Assert.Equal(new[] { 20, 30 }, write.ActivateOrder);
    }

    private sealed class BagStub : IFactorBagProbe
    {
        private readonly Dictionary<int, int> _c;
        public BagStub(int item, int count) => _c = new Dictionary<int, int> { [item] = count };
        public IReadOnlyDictionary<int, int> ReadFactorBagCounts(IReadOnlyCollection<int> itemIds) => _c;
    }

    // Socketing a factor is refused 7561 while a copy of it is still socketed in a foreign tree; freeing
    // that copy clears the limit.
    private sealed class ClassLimitFakeWrite : IDeepSlumberWriteProbe
    {
        private readonly HashSet<int> _foreignSocketed;
        public readonly List<string> Calls = new();
        public ClassLimitFakeWrite(params int[] foreignSocketedItems) => _foreignSocketed = new HashSet<int>(foreignSocketedItems);
        public bool IsResolved => true;
        public Task<int> EnableLineAsync(int a, CancellationToken ct) => Task.FromResult(0);
        public Task<int> ResetNodesAsync(int a, CancellationToken ct) => Task.FromResult(0);
        public Task<int> ActivateNodeAsync(int n, CancellationToken ct) => Task.FromResult(0);
        public Task<int> SocketFactorAsync(int n, int i, CancellationToken ct)
        {
            Calls.Add($"socket:{n}:{i}");
            return Task.FromResult(_foreignSocketed.Contains(i) ? DeepSlumberWriteCode.ItemClassNumExceeded : 0);
        }
        public Task<int> UnsocketFactorAsync(int n, int c, CancellationToken ct)
        {
            Calls.Add($"unsocket:{n}:{c}");
            _foreignSocketed.Remove(c);   // freeing the foreign copy clears the per-type limit
            return Task.FromResult(0);
        }
    }

    [Fact]
    public async Task SocketRefused7561_FreesForeignCopyThenRetries_Succeeds()
    {
        // Target area 6 wants 20020964 at an empty node; a copy is socketed in the INACTIVE area 5, and a
        // spare sits in the bag — so the predictive pass leaves the foreign copy in place (inventory check
        // first). The socket then hits 7561 (a copy in an inactive tree counts toward the per-type limit —
        // a bag spare can't clear it); the socket phase must free the foreign copy and retry, landing it.
        var src = new DeepSlumberArea(5, true, 0, new List<int[]>(), new List<int[]> { new[] { 141, 20020964 } }, new List<int[]>());
        var tgt = new DeepSlumberArea(6, true, 0, new List<int[]>(), new List<int[]>(), new List<int[]>());
        var read = new FakeRead
        {
            State = new DeepSlumberState(new List<int[]>(),
                new List<DeepSlumberLine> { new(3, 800522, new List<DeepSlumberArea> { src, tgt }) })
        };
        var write = new ClassLimitFakeWrite(20020964);
        var svc = new DeepSlumberService(read, write, new BagStub(20020964, 1));   // a spare is in the bag
        var target = new DeepSlumberSetup(1, new List<DeepSlumberAreaBinding> { new(6, new List<int[]> { new[] { 250, 20020964 } }) });

        var result = await svc.ApplySetupAsync(target);

        Assert.Equal(DeepSlumberApplyResult.Success, result);
        Assert.Contains(write.Calls, c => c == "unsocket:141:20020964");   // freed the foreign copy reactively
        Assert.Contains(write.Calls, c => c == "socket:250:20020964");
    }
}
