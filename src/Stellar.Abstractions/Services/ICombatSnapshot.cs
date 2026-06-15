using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Polled snapshot of the local player's combat state plus a ring buffer of
/// recently observed events. Mirrors the polled half of the original mixed
/// combat surface; the event stream lives on <see cref="ICombatEvents"/> and
/// per-entity lookups live on <see cref="ICombatLookup"/>. All reads are safe
/// from the Unity main thread.
/// </summary>
public interface ICombatSnapshot
{
    /// <summary>True once the wire tap has seen the first SyncToMeDeltaInfo after entering world.</summary>
    bool IsAvailable { get; }

    /// <summary>Local player's entity UUID. <see cref="EntityId.None"/> until first SyncToMeDeltaInfo.</summary>
    EntityId LocalEntityId { get; }

    /// <summary>Local player's current skill cooldowns. Empty out of combat / pre-login.</summary>
    IReadOnlyList<SkillCooldown> LocalCooldowns { get; }

    /// <summary>Active buffs on the local player.</summary>
    IReadOnlyList<ActiveBuff> LocalBuffs { get; }

    /// <summary>Latest server epoch (ms) seen on the SyncServerTime notify. Zero until first observation.</summary>
    long ServerNowMs { get; }

    /// <summary>Ring buffer of recent events (capacity 500).</summary>
    IReadOnlyList<CombatEvent> RecentEvents { get; }
}
