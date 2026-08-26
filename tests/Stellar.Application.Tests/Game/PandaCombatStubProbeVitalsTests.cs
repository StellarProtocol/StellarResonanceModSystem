using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Pins the pure per-write-site logic behind the 2026-08-26 raid-bosshp-capture-design wire fixes —
/// <see cref="PandaCombatStubProbe.ResolveMaxHp"/> (decision 1, AttrMaxHpTotal=11321 acceptance,
/// shared by BOTH vitals write sites) and <see cref="PandaCombatStubProbe.MapDisappearReason"/>
/// (decision 1, EDisappearType -> EntityDisappearReason). Both are `internal static` specifically so
/// they're testable without constructing the full probe (which needs an IL2CPP-backed
/// WorldNtfStubDispatcher to exercise end-to-end — see docs/il2cpp-probing-safety.md).
/// </summary>
public sealed class PandaCombatStubProbeVitalsTests
{
    // ── ResolveMaxHp: 11320 primary, 11321 fallback ──────────────────────────

    [Fact]
    public void ResolveMaxHp_BothPresent_PrefersMaxHpBase11320()
    {
        Assert.Equal(10_000L, PandaCombatStubProbe.ResolveMaxHp(maxHpBase: 10_000L, maxHpTotal: 12_000L));
    }

    [Fact]
    public void ResolveMaxHp_OnlyMaxHpTotal11321Present_UsesIt()
    {
        // The robustness case this fix adds: 11320 absent from THIS payload, 11321 present.
        Assert.Equal(12_000L, PandaCombatStubProbe.ResolveMaxHp(maxHpBase: -1L, maxHpTotal: 12_000L));
    }

    [Fact]
    public void ResolveMaxHp_OnlyMaxHpBasePresent_UsesIt()
    {
        Assert.Equal(10_000L, PandaCombatStubProbe.ResolveMaxHp(maxHpBase: 10_000L, maxHpTotal: -1L));
    }

    [Fact]
    public void ResolveMaxHp_NeitherPresent_ReturnsSentinel()
    {
        Assert.Equal(-1L, PandaCombatStubProbe.ResolveMaxHp(maxHpBase: -1L, maxHpTotal: -1L));
    }

    [Fact]
    public void ResolveMaxHp_MaxHpBaseZero_IsAValidObservation_NotAbsent()
    {
        // 0 is a real (if unusual) observed value, distinct from the -1 "absent" sentinel — must NOT
        // fall through to 11321.
        Assert.Equal(0L, PandaCombatStubProbe.ResolveMaxHp(maxHpBase: 0L, maxHpTotal: 12_000L));
    }

    // ── MapDisappearReason: wire int -> EntityDisappearReason ────────────────

    [Theory]
    [InlineData(0, EntityDisappearReason.Normal)]
    [InlineData(1, EntityDisappearReason.Dead)]
    [InlineData(2, EntityDisappearReason.Destroy)]
    [InlineData(3, EntityDisappearReason.TransferLeave)]
    [InlineData(4, EntityDisappearReason.Unknown)]  // real proto value (TransferPassLineLeave) — unnamed, safe default
    [InlineData(99, EntityDisappearReason.Unknown)] // garbage/future value
    [InlineData(-1, EntityDisappearReason.Unknown)]
    public void MapDisappearReason_MapsWireIntToDomainEnum(int wireType, EntityDisappearReason expected)
    {
        Assert.Equal(expected, PandaCombatStubProbe.MapDisappearReason(wireType));
    }
}
