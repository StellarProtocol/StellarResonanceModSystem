using System;
using System.Collections.Generic;
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

    [Fact]
    public void Several_probes_may_share_one_methodId_and_run_in_registration_order()
    {
        // PINNED (2026-08-23): WorldNtf method 3 now has TWO subscribers — PandaCombatStubProbe
        // (which latches the dungeon run id) and PandaWorldAttrProbe (whose Defeated seed is gated on
        // that very run id). Both must fire, and the run-id latch must go FIRST, so a "last
        // registration wins" router would silently drop one probe's handling of the packet.
        var r = new StubRouter();
        var order = new List<string>();
        r.Register(3, (_, _) => order.Add("run-id"));
        r.Register(3, (_, _) => order.Add("defeated-seed"));

        r.Route(3, new byte[] { 9 });

        Assert.Equal(new[] { "run-id", "defeated-seed" }, order);
    }
}
