using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public class DeepSlumberServiceTests
{
    private sealed class StubDeepSlumberProbe : IDeepSlumberProbe
    {
        public bool IsResolved { get; set; }
        public DeepSlumberState? State { get; set; }
        public DeepSlumberState? Read() => State;
    }

    // Not exercised by these read-passthrough tests; satisfies DeepSlumberService's 2-arg ctor
    // (write-side behaviour is covered by DeepSlumberServiceApplyTests).
    private sealed class StubDeepSlumberWriteProbe : IDeepSlumberWriteProbe
    {
        public bool IsResolved => true;
        public Task<int> EnableLineAsync(int areaId, CancellationToken ct) => Task.FromResult(0);
        public Task<int> SocketFactorAsync(int nodeId, int itemId, CancellationToken ct) => Task.FromResult(0);
        public Task<int> UnsocketFactorAsync(int nodeId, int currentItemId, CancellationToken ct) => Task.FromResult(0);
    }

    [Fact]
    public void UnresolvedProbe_IsUnavailable_AndStateNull()
    {
        var service = new DeepSlumberService(new StubDeepSlumberProbe(), new StubDeepSlumberWriteProbe());
        Assert.False(service.IsAvailable);
        Assert.Null(service.GetState());
    }

    [Fact]
    public void ResolvedProbe_PassesStateThrough()
    {
        var line = new DeepSlumberLine(93, 3, new[]
        {
            new DeepSlumberArea(1, true, 120,
                new[] { new[] { 11, 5110001 } },
                Array.Empty<int[]>(),
                new[] { new[] { 21, 4 } }),
        });
        var state = new DeepSlumberState(new[] { new[] { 93, 65 } }, new[] { line });
        var probe = new StubDeepSlumberProbe { IsResolved = true, State = state };
        var service = new DeepSlumberService(probe, new StubDeepSlumberWriteProbe());
        Assert.True(service.IsAvailable);
        Assert.Same(state, service.GetState());
    }
}
