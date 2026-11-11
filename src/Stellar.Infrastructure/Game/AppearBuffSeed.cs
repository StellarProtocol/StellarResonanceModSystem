using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game.Protobuf;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Hands one appear/EnterScene entity's buff snapshot to the combat sink — only when the reader decoded it
/// completely (spec-from-talent-buffs review fix D, 2026-09-26). An "unknown" snapshot (truncated framing or a
/// malformed <c>BuffInfo</c>) is skipped so buffs the client could not read are never wiped; field 7 absent
/// (<c>Buffs == null</c>) is a real empty set and does replace. The entity's buff effect ids (field 7 BuffInfos +
/// field 8 BuffEffectSync) are applied either way: they are per-instance updates, not a complete set. Pure so it is
/// unit-testable without the probe.
/// </summary>
internal static class AppearBuffSeed
{
    /// <summary>Returns true when the snapshot was applied, false when it was skipped as unknown.</summary>
    public static bool Apply(ICombatBuffSink sink, EntityId entityId, in AppearEntityMsg entity, long timestampMs)
    {
        if (entity.EffectIds is { Count: > 0 } fx) sink.ApplyBuffEffectIds(entityId, fx);
        if (entity.BuffsUnknown) return false;
        sink.ReplaceEntityBuffs(entityId, entity.Buffs, timestampMs);
        return true;
    }
}
