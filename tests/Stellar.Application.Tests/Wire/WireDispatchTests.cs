using System;
using System.Collections.Generic;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Unit tests for <see cref="PandaWireTap"/> dispatch table. The wire-tap's
/// recv hook + parse loop are exercised in-game via the chat regression
/// scenario; these tests cover the pure-managed registration / dispatch
/// surface so we can refactor the routing logic without paying a full
/// install + scenario cycle.
/// </summary>
public sealed class WireDispatchTests
{
    [Fact]
    public void Dispatch_RoutesEnvelopeToMatchingHandlerOnly()
    {
        var tap = new PandaWireTap(new StubLog());
        var receivedA = new List<WireEnvelope>();
        var receivedB = new List<WireEnvelope>();

        tap.Register(serviceUuid: 100, methodId: 1, e => receivedA.Add(e));
        tap.Register(serviceUuid: 200, methodId: 1, e => receivedB.Add(e));

        tap.DispatchForTest(new WireEnvelope
        {
            Kind = WireMessageKind.Notify,
            ServiceUuid = 100,
            MethodId = 1,
            Payload = new byte[] { 0xAB },
        });

        Assert.Single(receivedA);
        Assert.Empty(receivedB);
        Assert.Equal(new byte[] { 0xAB }, receivedA[0].Payload.ToArray());
    }

    [Fact]
    public void Dispatch_MultipleHandlersForSameKey_AllFire()
    {
        var tap = new PandaWireTap(new StubLog());
        int countA = 0, countB = 0;

        tap.Register(50, 5, _ => countA++);
        tap.Register(50, 5, _ => countB++);

        tap.DispatchForTest(new WireEnvelope { ServiceUuid = 50, MethodId = 5 });

        Assert.Equal(1, countA);
        Assert.Equal(1, countB);
    }

    [Fact]
    public void Dispatch_NoHandlerForKey_DoesNotThrow()
    {
        var tap = new PandaWireTap(new StubLog());

        // Should silently no-op — no exception.
        tap.DispatchForTest(new WireEnvelope { ServiceUuid = 1, MethodId = 1 });
    }

    [Fact]
    public void Dispatch_HandlerThrows_OtherHandlersStillFire_AndIsLogged()
    {
        var log = new StubLog();
        var tap = new PandaWireTap(log);
        int afterThrowCount = 0;

        tap.Register(7, 7, _ => throw new InvalidOperationException("boom"));
        tap.Register(7, 7, _ => afterThrowCount++);

        tap.DispatchForTest(new WireEnvelope { ServiceUuid = 7, MethodId = 7 });

        Assert.Equal(1, afterThrowCount);
        Assert.Contains(log.WarningLines, line => line.Contains("[WireTap] handler threw"));
    }

    [Fact]
    public void Register_NullHandler_Throws()
    {
        var tap = new PandaWireTap(new StubLog());
        Assert.Throws<ArgumentNullException>(() => tap.Register(1, 1, null!));
    }
}
