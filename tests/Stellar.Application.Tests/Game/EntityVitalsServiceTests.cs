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
    public void Reset_OffGame_DoesNotThrow()
    {
        // I1 review fix's Reset() — wired at scene-change (OnEnterScene, network thread) and logout —
        // must be safe to call even when nothing was ever tracked (off-game, or a scene change before
        // any TryGetBlood call).
        var svc = NewService();
        svc.Reset();
    }

    [Fact]
    public void Reset_OffGame_AfterFailedTryGetBlood_DoesNotThrow()
    {
        var svc = NewService();
        svc.TryGetBlood(new EntityId(123), out _, out _);
        svc.Reset();
        Assert.False(svc.TryGetBlood(new EntityId(123), out var pct, out var stage));
        Assert.Equal(0, pct);
        Assert.Equal(0, stage);
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

    // ── ToFloat (pure) — field-failure fix, sea/WwLG5Bq4ni point 4 ────────────
    // Must distinguish "unconvertible" from a genuine 0.0 reading — the OLD version defaulted both to
    // 0f, which is exactly what let a masked read report a false 0%.

    [Fact]
    public void ToFloat_Float_Converts() => Assert.Equal(0.5f, EntityVitalsService.ToFloat(0.5f));

    [Fact]
    public void ToFloat_Double_Converts() => Assert.Equal(3.5f, EntityVitalsService.ToFloat(3.5d));

    [Fact]
    public void ToFloat_Int_Converts() => Assert.Equal(7f, EntityVitalsService.ToFloat(7));

    [Fact]
    public void ToFloat_Long_Converts() => Assert.Equal(9f, EntityVitalsService.ToFloat(9L));

    [Fact]
    public void ToFloat_Null_ReturnsNull_NotZero() => Assert.Null(EntityVitalsService.ToFloat(null));

    [Fact]
    public void ToFloat_UnconvertibleObject_ReturnsNull_NotZero() => Assert.Null(EntityVitalsService.ToFloat(new object()));

    // ── GetOrResolveNullableHandles / TryUnwrap — the unwrap-decision seam ────
    // Field-failure fix, sea/WwLG5Bq4ni point 1: Il2CppInterop's generated Nullable<T> wrapper is a
    // REAL non-null object even when HasValue=false — pure reflection over plain C# test doubles
    // shaped the same way (no IL2Cpp needed; the detection/unwrap logic is generic).

    private sealed class WrapperViaProperties
    {
        public bool HasValue { get; set; }
        public object? Value { get; set; }
    }

    private sealed class WrapperViaFields
    {
        public bool HasValue;
        public object? Value;
    }

    private sealed class NotAWrapperShape
    {
        public int Unrelated;
    }

    [Fact]
    public void GetOrResolveNullableHandles_PropertyShape_DetectsWrapper()
    {
        var handles = NewService().GetOrResolveNullableHandles(typeof(WrapperViaProperties));
        Assert.True(handles.IsNullableWrapper);
        Assert.NotNull(handles.HasValueProperty);
        Assert.NotNull(handles.ValueProperty);
        Assert.Null(handles.HasValueField);
        Assert.Null(handles.ValueField);
    }

    [Fact]
    public void GetOrResolveNullableHandles_FieldShape_DetectsWrapperViaFieldFallback()
    {
        var handles = NewService().GetOrResolveNullableHandles(typeof(WrapperViaFields));
        Assert.True(handles.IsNullableWrapper);
        Assert.NotNull(handles.HasValueField);
        Assert.NotNull(handles.ValueField);
    }

    [Fact]
    public void GetOrResolveNullableHandles_NoMatchingShape_NotAWrapper()
    {
        var handles = NewService().GetOrResolveNullableHandles(typeof(NotAWrapperShape));
        Assert.False(handles.IsNullableWrapper);
    }

    [Fact]
    public void TryUnwrap_NonWrapperObject_ReturnsItUnchanged()
    {
        // Preserves the original managed-boxing assumption as a fallback for a type with no
        // HasValue+Value shape — treated as already BEING the value.
        var svc = NewService();
        var raw = new NotAWrapperShape { Unrelated = 42 };

        var ok = svc.TryUnwrap(raw, out var result);

        Assert.True(ok);
        Assert.Same(raw, result);
    }

    [Fact]
    public void TryUnwrap_WrapperHasValueTrue_ReturnsUnwrappedValue()
    {
        var svc = NewService();
        var inner = new object();
        var wrapper = new WrapperViaProperties { HasValue = true, Value = inner };

        var ok = svc.TryUnwrap(wrapper, out var result);

        Assert.True(ok);
        Assert.Same(inner, result);
    }

    [Fact]
    public void TryUnwrap_WrapperHasValueFalse_ReturnsFalse()
    {
        // THE root-cause case: a non-null invoke result whose HasValue is false is NOT a real
        // observation — the OLD code treated any non-null result as success.
        var svc = NewService();
        var wrapper = new WrapperViaProperties { HasValue = false, Value = new object() };

        var ok = svc.TryUnwrap(wrapper, out var result);

        Assert.False(ok);
        Assert.Null(result);
    }

    [Fact]
    public void TryUnwrap_FieldShapeWrapper_UnwrapsViaFields()
    {
        var svc = NewService();
        var inner = new object();
        var wrapper = new WrapperViaFields { HasValue = true, Value = inner };

        var ok = svc.TryUnwrap(wrapper, out var result);

        Assert.True(ok);
        Assert.Same(inner, result);
    }

    // ── ResolveBloodFields — the field-resolution seam ────────────────────────
    // Field-failure fix, sea/WwLG5Bq4ni point 2: resolved + cached PER Type, not latched globally.

    private sealed class BloodDataViaFields
    {
        public float BloodPercent;
        public int Stage;
    }

    private sealed class BloodDataViaProperties
    {
        public float BloodPercent { get; set; }
        public int Stage { get; set; }
    }

    private sealed class BloodDataMissing
    {
        public int Unrelated;
    }

    [Fact]
    public void ResolveBloodFields_FieldShape_ResolvesViaFields()
    {
        var handles = NewService().ResolveBloodFields(typeof(BloodDataViaFields));
        Assert.True(handles.PercentReadable);
        Assert.True(handles.StageReadable);
        Assert.NotNull(handles.PercentField);
        Assert.NotNull(handles.StageField);
    }

    [Fact]
    public void ResolveBloodFields_PropertyShape_ResolvesViaProperties()
    {
        var handles = NewService().ResolveBloodFields(typeof(BloodDataViaProperties));
        Assert.True(handles.PercentReadable);
        Assert.True(handles.StageReadable);
        Assert.NotNull(handles.PercentProperty);
        Assert.NotNull(handles.StageProperty);
    }

    [Fact]
    public void ResolveBloodFields_MissingShape_RecordsUnreadable_PerTypeNotGlobally()
    {
        // THE latch bug this fix corrects: a miss for one type must not poison resolution for a
        // DIFFERENT, readable type looked up afterwards.
        var svc = NewService();

        var missing = svc.ResolveBloodFields(typeof(BloodDataMissing));
        Assert.False(missing.PercentReadable);
        Assert.False(missing.StageReadable);

        var readable = svc.ResolveBloodFields(typeof(BloodDataViaFields));
        Assert.True(readable.PercentReadable);
        Assert.True(readable.StageReadable);
    }
}
