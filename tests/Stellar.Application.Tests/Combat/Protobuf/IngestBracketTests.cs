using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game;
using Xunit;

// Cross-thread pins deliberately block on a bounded Task.Wait: the point is to prove a lock is (or is not)
// held by another thread, which an async test cannot express deterministically.
#pragma warning disable xUnit1031

namespace Stellar.Application.Tests.Combat.Protobuf;

/// <summary>
/// m2 (final review round 2026-09-26): the probe routes every WorldNtf packet through
/// <see cref="IngestBracket.Run"/>; a handler that throws must still release the packet bracket, or the
/// main-thread spec publish would be skipped forever (and a logout reset would deadlock).
/// </summary>
public sealed class IngestBracketTests
{
    private static readonly EntityId P = new((4242L << 16) | 640L);

    [Fact]
    public void ThrowingRoute_StillReleasesTheBracket()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        var ev = new List<CombatEvent.SpecChanged>();
        svc.CombatEventOccurred += e => { if (e is CombatEvent.SpecChanged sc) ev.Add(sc); };

        Assert.Throws<InvalidOperationException>(() => IngestBracket.Run(svc, 3, Array.Empty<byte>(), (_, _) =>
        {
            svc.ApplyBuffEvents(P, new[] { new ActiveBuff(1, 2202110, 1, P, 1, 1, 0, 0, 6, 1) }, Array.Empty<int>(), 1000);
            throw new InvalidOperationException("handler bug");
        }));

        // From ANOTHER thread (a leaked Monitor would still let this thread re-enter): must publish.
        var drain = System.Threading.Tasks.Task.Run(svc.Drain);
        Assert.True(drain.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(50001, Assert.Single(ev).NewSubProfessionId);

        var reset = System.Threading.Tasks.Task.Run(svc.ResetEntities);
        Assert.True(reset.Wait(TimeSpan.FromSeconds(5)), "bracket leaked: reset blocked");
    }

    [Fact]
    public void Run_PassesMethodAndPayloadThrough()
    {
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());
        var payload = new byte[] { 1, 2, 3 };
        (uint, byte[])? seen = null;
        IngestBracket.Run(svc, 45, payload, (m, p) => seen = (m, p));
        Assert.Equal(45u, seen!.Value.Item1);
        Assert.Same(payload, seen.Value.Item2);
    }
}
