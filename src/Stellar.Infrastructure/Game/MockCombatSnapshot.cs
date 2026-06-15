using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Deterministic <see cref="ICombatSnapshot"/> implementation activated by the
/// <c>STELLAR_MOCK_COOLDOWNS=1</c> environment variable. Returns a fixed
/// 6-skill cooldown set with a mix of ready / cooling / charge states so the
/// CooldownBar plugin can render outside of real combat for visual-capture
/// scenarios (Phase 9a.5 visual verification toolkit).
///
/// The snapshot is static — no <see cref="CombatEventOccurred"/>-style events
/// fire and the values never animate. Visual scenarios call into this from
/// the title / character-select screens where the real combat wire is silent.
///
/// Production code path is unaffected when the env var is absent — the mock
/// is wired in <c>BootstrapPlugin.WireGameEventsAndPluginHost</c> only when
/// the env var equals <c>"1"</c>.
/// </summary>
internal sealed class MockCombatSnapshot : ICombatSnapshot
{
    // Arbitrary fixed server epoch — large enough that BeginTimeMs values
    // below remain positive when subtracted from it.
    private const long FixedServerNow = 1_000_000;

    private static readonly IReadOnlyList<SkillCooldown> Fixture = new[]
    {
        // Ready (BeginTimeMs=0) — empty slot rendered as available.
        new SkillCooldown(SkillId: 101, BeginTimeMs: 0,                       DurationMs: 5000,  Kind: SkillCooldownKind.Normal, ChargeCount: 0, ValidCdTimeMs: 0),
        // Mid-cooldown (3.5s into a 5s normal CD).
        new SkillCooldown(SkillId: 102, BeginTimeMs: FixedServerNow - 3500,   DurationMs: 5000,  Kind: SkillCooldownKind.Normal, ChargeCount: 0, ValidCdTimeMs: 5000),
        // Charge skill with 2 charges banked, currently regenerating one.
        new SkillCooldown(SkillId: 103, BeginTimeMs: FixedServerNow - 1500,   DurationMs: 10000, Kind: SkillCooldownKind.Charge, ChargeCount: 2, ValidCdTimeMs: 10000),
        // Ready (BeginTimeMs=0).
        new SkillCooldown(SkillId: 104, BeginTimeMs: 0,                       DurationMs: 8000,  Kind: SkillCooldownKind.Normal, ChargeCount: 0, ValidCdTimeMs: 0),
        // Late in cooldown (4.5s into a 6s normal CD).
        new SkillCooldown(SkillId: 105, BeginTimeMs: FixedServerNow - 4500,   DurationMs: 6000,  Kind: SkillCooldownKind.Normal, ChargeCount: 0, ValidCdTimeMs: 6000),
        // Ready (BeginTimeMs=0).
        new SkillCooldown(SkillId: 106, BeginTimeMs: 0,                       DurationMs: 4000,  Kind: SkillCooldownKind.Normal, ChargeCount: 0, ValidCdTimeMs: 0),
    };

    private static readonly EntityId MockLocalEntity = new EntityId(0xDEAD_BEEFL);

    public bool IsAvailable => true;
    public EntityId LocalEntityId => MockLocalEntity;
    public IReadOnlyList<SkillCooldown> LocalCooldowns => Fixture;
    public IReadOnlyList<ActiveBuff> LocalBuffs => Array.Empty<ActiveBuff>();
    public long ServerNowMs => FixedServerNow;
    public IReadOnlyList<CombatEvent> RecentEvents => Array.Empty<CombatEvent>();
}
