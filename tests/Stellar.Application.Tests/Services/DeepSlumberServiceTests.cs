using System;
using System.Collections.Generic;
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

    [Fact]
    public void UnresolvedProbe_IsUnavailable_AndStateNull()
    {
        var service = new DeepSlumberService(new StubDeepSlumberProbe());
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
        var service = new DeepSlumberService(probe);
        Assert.True(service.IsAvailable);
        Assert.Same(state, service.GetState());
    }
}
