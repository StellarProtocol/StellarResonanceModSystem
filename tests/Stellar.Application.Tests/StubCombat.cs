using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Tests;

/// <summary>
/// In-memory combat surface for service tests. Implements the segregated
/// <see cref="ICombatSnapshot"/> / <see cref="ICombatLookup"/> / <see cref="ICombatEvents"/>
/// trio so tests can inject the stub through whichever sub-interface the
/// service-under-test depends on. Raise() invokes CombatEventOccurred subscribers.
/// </summary>
internal sealed class StubCombat : ICombatSnapshot, ICombatLookup, ICombatEvents
{
    public bool IsAvailable { get; set; } = true;
    public EntityId LocalEntityId { get; set; } = EntityId.None;
    public IReadOnlyList<SkillCooldown> LocalCooldowns { get; set; } = Array.Empty<SkillCooldown>();
    public IReadOnlyList<ActiveBuff> LocalBuffs { get; set; } = Array.Empty<ActiveBuff>();
    public long ServerNowMs { get; set; }
    public IReadOnlyList<CombatEvent> RecentEvents { get; set; } = Array.Empty<CombatEvent>();

    public event Action<CombatEvent>? CombatEventOccurred;

    public IReadOnlyList<ActiveBuff> BuffsFor(EntityId entityId) => Array.Empty<ActiveBuff>();
    public string? GetEntityName(EntityId entityId) => null;
    public EntityVitals GetVitals(EntityId entityId) => EntityVitals.Unknown;
    public long GetLiveDps(EntityId sourceId) => 0;
    public long GetLiveHps(EntityId sourceId) => 0;
    public long GetTeamId(EntityId entityId) => 0;
    public long GetFightPoint(EntityId entityId) => 0;
    public IReadOnlyList<SkillLevel> GetSkillLevels(EntityId entityId) => System.Array.Empty<SkillLevel>();

    public void Raise(CombatEvent evt) => CombatEventOccurred?.Invoke(evt);
}
