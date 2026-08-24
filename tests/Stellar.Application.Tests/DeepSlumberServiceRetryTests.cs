using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

/// <summary>Covers the speed/reliability behaviour added to <see cref="DeepSlumberService"/>: retry a
/// transient (dropped) op, NEVER retry a positive game refusal, and hold the unsocket→socket phase
/// barrier when the ops pipeline.</summary>
public sealed class DeepSlumberServiceRetryTests
{
    private sealed class FakeRead : IDeepSlumberProbe
    {
        public DeepSlumberState? State;
        public bool IsResolved => true;
        public DeepSlumberState? Read() => State;
    }

    // Records each call in order and returns a per-verb programmed code sequence (empty → 0).
    private sealed class SeqWrite : IDeepSlumberWriteProbe
    {
        public bool IsResolved => true;
        public readonly List<string> Calls = new();
        public readonly Queue<int> EnableCodes = new();
        public readonly Queue<int> SocketCodes = new();
        public readonly Queue<int> UnsocketCodes = new();

        private static int Next(Queue<int> q) => q.Count > 0 ? q.Dequeue() : DeepSlumberWriteCode.Ok;

        public Task<int> EnableLineAsync(int a, CancellationToken ct) { Calls.Add($"enable:{a}"); return Task.FromResult(Next(EnableCodes)); }
        public Task<int> SocketFactorAsync(int n, int i, CancellationToken ct) { Calls.Add($"socket:{n}"); return Task.FromResult(Next(SocketCodes)); }
        public Task<int> UnsocketFactorAsync(int n, int c, CancellationToken ct) { Calls.Add($"unsocket:{n}"); return Task.FromResult(Next(UnsocketCodes)); }
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

    [Fact]
    public async Task TransientCode_IsRetried_ThenSucceeds()
    {
        // The first dispatch is dropped (Timeout sentinel), the retry lands (Ok). Result = Success, and
        // the op was actually re-fired (two socket calls) — this is the "server dropped it" recovery.
        var write = new SeqWrite();
        write.SocketCodes.Enqueue(DeepSlumberWriteCode.Timeout);
        write.SocketCodes.Enqueue(DeepSlumberWriteCode.Ok);
        var svc = new DeepSlumberService(new FakeRead { State = LiveActive(5) }, write);

        Assert.Equal(DeepSlumberApplyResult.Success, await svc.ApplySetupAsync(Target(5, (118, 222))));
        Assert.Equal(new[] { "socket:118", "socket:118" }, write.Calls);
    }

    [Fact]
    public async Task PositiveRefusal_IsNeverRetried()
    {
        // 7561 = a DETERMINISTIC game refusal (item unavailable), not a drop. It must fire exactly once
        // — retrying would fail identically and could misreport a real success as a failure.
        var write = new SeqWrite();
        write.SocketCodes.Enqueue(7561);
        var svc = new DeepSlumberService(new FakeRead { State = LiveActive(5) }, write);

        Assert.Equal(DeepSlumberApplyResult.Refused, await svc.ApplySetupAsync(Target(5, (118, 222))));
        Assert.Single(write.Calls);
    }

    [Fact]
    public async Task TransientRetriesExhaust_ThenFails_BoundedAttempts()
    {
        // A persistently-dropped op retries a bounded number of times (initial + MaxRetries) and then
        // gives up — never an unbounded storm.
        var write = new SeqWrite();
        for (var i = 0; i < 10; i++) write.SocketCodes.Enqueue(DeepSlumberWriteCode.Timeout);
        var svc = new DeepSlumberService(new FakeRead { State = LiveActive(5) }, write);

        Assert.Equal(DeepSlumberApplyResult.Refused, await svc.ApplySetupAsync(Target(5, (118, 222))));
        Assert.Equal(3, write.Calls.Count); // 1 initial + 2 retries
    }

    [Fact]
    public async Task PhaseBarrier_AllUnsocketsPrecedeAllSockets()
    {
        // Two nodes each swap factor → 2 unsockets + 2 sockets. Even pipelined, EVERY unsocket must land
        // before ANY socket (scarce single-copy factors freed before they are re-socketed elsewhere).
        var write = new SeqWrite();
        var live = LiveActive(5, (118, 111), (119, 222));
        var svc = new DeepSlumberService(new FakeRead { State = live }, write);

        Assert.Equal(DeepSlumberApplyResult.Success, await svc.ApplySetupAsync(Target(5, (118, 333), (119, 444))));

        var lastUnsocket = write.Calls.FindLastIndex(c => c.StartsWith("unsocket:"));
        var firstSocket = write.Calls.FindIndex(c => c.StartsWith("socket:"));
        Assert.True(lastUnsocket >= 0 && firstSocket >= 0, "expected both unsocket and socket calls");
        Assert.True(lastUnsocket < firstSocket, $"a socket ran before an unsocket: {string.Join(",", write.Calls)}");
    }
}
