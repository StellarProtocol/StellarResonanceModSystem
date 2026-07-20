using System;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Pins the wire-position fallback TRIGGER — origin: run sea/223237664013287424 (Thanatos walk-in).
/// The measured un-settled GO reads are NEAR-ORIGIN JITTER (X 0→37, Y≈0, Z ±14), not exact zeros, so
/// the zero-sentinel predicate alone never engaged and the (verified-correct) wire cache went unused
/// ([PosDbg][fallback] count = 0 over that run). The trigger now also fires on VERTICAL floor
/// disagreement vs a fresh wire sample.
/// </summary>
public sealed class EntityTransformsFallbackTests
{
    // Real numbers from run sea/223237664013287424.
    private static readonly Position3D JitterGo = new(12f, 0.3f, -8f);          // near-origin jitter (NOT zero-sentinel)
    private static readonly Position3D WireFloor = new(150.8f, 100.2f, -304.1f); // fresh wire (true boss-room floor)

    private sealed class StubTypeRegistry : IGameTypeRegistry
    {
        public Type? FindType(string fullName) => null;
    }

    private static EntityTransformsService NewService(WireEntityPositions cache, StubLog log)
        => new(new StubTypeRegistry(), cache, log);

    // ── Pure decision (ShouldSubstituteFreshWire) ────────────────────────────

    [Fact]
    public void Decision_ZeroSentinelGo_SubstitutesWire()
        => Assert.True(EntityTransformsService.ShouldSubstituteFreshWire(Position3D.Zero, WireFloor));

    [Fact]
    public void Decision_NearOriginJitterGo_FloorDisagrees_SubstitutesWire()
        => Assert.True(EntityTransformsService.ShouldSubstituteFreshWire(JitterGo, WireFloor));

    [Fact]
    public void Decision_SettledGo_AgreesVertically_KeepsGo()
    {
        // Both on the real floor (ΔY ≈ 0.4 m) — GO stays primary even though X/Z differ ~5 m.
        var settledGo = new Position3D(170f, 100.5f, -290f);
        var wire = new Position3D(165f, 100.1f, -295f);
        Assert.False(EntityTransformsService.ShouldSubstituteFreshWire(settledGo, wire));
    }

    [Fact]
    public void Decision_DegenerateWire_NeverSubstitutes()
        => Assert.False(EntityTransformsService.ShouldSubstituteFreshWire(JitterGo, Position3D.Zero));

    [Fact]
    public void Decision_FloorDisagreementBoundary()
    {
        var go = new Position3D(0f, 100f, 0f);
        // Exactly 5 m → not > threshold → keep GO; 5.1 m → substitute.
        Assert.False(EntityTransformsService.ShouldSubstituteFreshWire(go, new Position3D(0f, 105f, 0f)));
        Assert.True(EntityTransformsService.ShouldSubstituteFreshWire(go, new Position3D(0f, 105.1f, 0f)));
    }

    // ── Instance path (MaybeApplyWireFallback) ───────────────────────────────

    [Fact]
    public void Fallback_JitterGoWithFreshWire_SubstitutesAndFiresOneShot()
    {
        var cache = new WireEntityPositions(() => 0);
        var log = new StubLog();
        var svc = NewService(cache, log);
        cache.OnPosition(555, new WirePos(WireFloor.X, WireFloor.Y, WireFloor.Z, 0f, false));

        var pos = JitterGo;
        var yaw = 0f;
        svc.MaybeApplyWireFallback(new EntityId(555), ref pos, ref yaw);

        Assert.Equal(WireFloor.X, pos.X, 3);
        Assert.Equal(WireFloor.Y, pos.Y, 3);
        Assert.Equal(WireFloor.Z, pos.Z, 3);
        // (e) the one-shot fallback-engaged line fires on the new (floor-disagreement) branch.
        Assert.Contains(log.InfoLines, l => l.Contains("wire-position fallback engaged for entity 555"));
    }

    [Fact]
    public void Fallback_NoFreshWire_KeepsGo_FailsSafe()
    {
        var cache = new WireEntityPositions(() => 0); // empty
        var log = new StubLog();
        var svc = NewService(cache, log);

        var pos = JitterGo;
        var yaw = 0f;
        svc.MaybeApplyWireFallback(new EntityId(555), ref pos, ref yaw);

        Assert.Equal(JitterGo.X, pos.X, 3); // unchanged — today's behavior
        Assert.Equal(JitterGo.Y, pos.Y, 3);
        Assert.Empty(log.InfoLines);
    }

    [Fact]
    public void Fallback_SettledGo_KeepsGo()
    {
        var cache = new WireEntityPositions(() => 0);
        var log = new StubLog();
        var svc = NewService(cache, log);
        cache.OnPosition(555, new WirePos(165f, 100.1f, -295f, 0f, false));

        var pos = new Position3D(170f, 100.5f, -290f);
        var yaw = 0f;
        svc.MaybeApplyWireFallback(new EntityId(555), ref pos, ref yaw);

        Assert.Equal(170f, pos.X, 3); // GO kept — agrees vertically with fresh wire
        Assert.Empty(log.InfoLines);
    }
}
