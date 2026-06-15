using System;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Unit tests for <see cref="StubRouter"/> — pure methodId-keyed handler registry.
/// No IL2CPP / BepInEx / Unity dependencies.
/// </summary>
public sealed class StubRouterTests
{
    [Fact]
    public void Route_calls_only_the_handler_registered_for_that_methodId()
    {
        var r = new StubRouter();
        uint seen = 0; byte[]? got = null;
        r.Register(42, (m, p) => { seen = m; got = p; });
        r.Register(99, (m, p) => throw new Exception("must not fire"));
        r.Route(42, new byte[] { 1, 2, 3 });
        Assert.Equal(42u, seen);
        Assert.Equal(new byte[] { 1, 2, 3 }, got);
        r.Route(7, Array.Empty<byte>()); // unregistered → no-op, no throw
    }

    [Fact]
    public void Subscribes_returns_false_for_unregistered_methodId()
    {
        var r = new StubRouter();
        r.Register(42, (_, _) => { });
        Assert.True(r.Subscribes(42));
        Assert.False(r.Subscribes(43));
    }
}
