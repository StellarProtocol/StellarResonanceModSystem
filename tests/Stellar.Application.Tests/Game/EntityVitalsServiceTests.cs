using System;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Off-game safety contract + pure-logic tests for <see cref="EntityVitalsService"/>
/// (2026-08-26 raid-bosshp-capture-design § decision 2). The real service requires a live IL2CPP game
/// process (reflects <c>Panda.ZGame.ZEntityMgr</c> / <c>Panda.ZUi.BossBloodUtil</c>) — mirrors
/// <c>EntityTransformsServiceContractTests</c>/<c>EntityTransformsFallbackTests</c>: an off-game
/// <see cref="IGameTypeRegistry"/> stub (returns null for every type) pins the defensive contract,
/// and <see cref="EntityVitalsService.ToPercentInt"/> — the one piece of genuinely pure logic in the
/// service — gets its own direct coverage.
/// </summary>
public sealed class EntityVitalsServiceTests
{
    private sealed class StubTypeRegistry : IGameTypeRegistry
    {
        public Type? FindType(string fullName) => null;
    }

    private static EntityVitalsService NewService() => new(
        new StubTypeRegistry(),
        new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester()),
        new StubLog());

    // ── Off-game contract ─────────────────────────────────────────────────────

    [Fact]
    public void TryGetBlood_OffGame_ReturnsFalseAndDefaults()
    {
        var svc = NewService();
        var ok = svc.TryGetBlood(new EntityId(123), out var pct, out var stage);
        Assert.False(ok);
        Assert.Equal(0, pct);
        Assert.Equal(0, stage);
    }

    [Fact]
    public void TryGetBlood_NoneEntityId_ReturnsFalse()
    {
        var svc = NewService();
        Assert.False(svc.TryGetBlood(EntityId.None, out _, out _));
    }

    [Fact]
    public void IsBoss_OffGame_ReturnsFalse()
    {
        var svc = NewService();
        Assert.False(svc.IsBoss(new EntityId(123)));
    }

    [Fact]
    public void Tick_OffGame_NoTrackedIds_DoesNotThrow()
    {
        var svc = NewService();
        svc.Tick();
    }

    [Fact]
    public void Tick_OffGame_AfterFailedTryGetBlood_StillDoesNotThrow()
    {
        // TryGetBlood off-game returns false before ever reaching TrackForWatcher (core handles never
        // resolve) — Tick() must still be a safe no-op afterwards.
        var svc = NewService();
        svc.TryGetBlood(new EntityId(123), out _, out _);
        svc.Tick();
    }

    // ── ToPercentInt (pure) ────────────────────────────────────────────────────
    // UNVERIFIED headless whether BossBloodLogicData.BloodPercent ships 0..100 or 0..1 — see the
    // method's own doc. Pins the chosen normalization + clamp behavior either way.

    [Theory]
    [InlineData(0.5f, 50)]
    [InlineData(1f, 100)]    // boundary: exactly 1 treated as a fraction
    [InlineData(0.0f, 0)]
    [InlineData(37f, 37)]    // already-percent form (> 1)
    [InlineData(100f, 100)]
    [InlineData(150f, 100)]  // clamp above 100
    [InlineData(-5f, 0)]     // clamp below 0
    public void ToPercentInt_NormalizesFractionOrPercentAndClamps(float raw, int expected)
    {
        Assert.Equal(expected, EntityVitalsService.ToPercentInt(raw));
    }
}
