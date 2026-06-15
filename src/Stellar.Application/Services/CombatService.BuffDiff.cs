using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Buff event accumulation. AoiSyncDelta field 10 (BuffEffectSync) is an EVENT
/// stream — each delta carries only the buffs added/refreshed/removed this tick,
/// keyed by BuffUuid. We maintain a per-entity buff set, emit BuffChanged events,
/// and refresh the cached local-buff snapshot. Split from CombatService.cs to
/// keep that file under the 500-LoC threshold.
/// </summary>
internal sealed partial class CombatService
{
    public void ApplyBuffEvents(
        EntityId entityId,
        IReadOnlyList<ActiveBuff> upserts,
        IReadOnlyList<int> removedBuffUuids,
        long timestampMs)
    {
        bool changed = false;
        lock (_buffsByEntityLock)
        {
            if (!_buffsByEntity.TryGetValue(entityId, out var set))
            {
                set = new Dictionary<int, ActiveBuff>();
                _buffsByEntity[entityId] = set;
            }

            for (int i = 0; i < upserts.Count; i++)
            {
                var b = upserts[i];
                if (set.TryGetValue(b.BuffUuid, out var prev))
                {
                    var merged = MergeNonZero(prev, b);
                    if (merged.Equals(prev)) continue;   // no-op refresh — emit nothing
                    set[b.BuffUuid] = merged;
                    EnqueueEvent(new CombatEvent.BuffChanged(
                        timestampMs, entityId, merged.BuffUuid, merged.BaseId,
                        BuffChangeKind.Refreshed, merged.Stacks, merged.Layer, merged.DurationMs));
                }
                else
                {
                    set[b.BuffUuid] = b;
                    EnqueueEvent(new CombatEvent.BuffChanged(
                        timestampMs, entityId, b.BuffUuid, b.BaseId,
                        BuffChangeKind.Applied, b.Stacks, b.Layer, b.DurationMs));
                }
                changed = true;
            }

            for (int i = 0; i < removedBuffUuids.Count; i++)
            {
                int uuid = removedBuffUuids[i];
                if (set.Remove(uuid, out var old))
                {
                    EnqueueEvent(new CombatEvent.BuffChanged(
                        timestampMs, entityId, old.BuffUuid, old.BaseId,
                        BuffChangeKind.Removed, old.Stacks, old.Layer, old.DurationMs));
                    changed = true;
                }
            }

            if (changed && entityId == _localEntityId)
                _localBuffs = new List<ActiveBuff>(set.Values);
        }
    }

    // Overwrite cur with next's non-default scalar fields. Partial BuffChange
    // upserts (BaseId=0; only layer/duration/createtime) merge onto the existing
    // entry so they never clobber the real BaseId/CreateTime set at add time.
    private static ActiveBuff MergeNonZero(ActiveBuff cur, ActiveBuff next) => new(
        next.BuffUuid != 0 ? next.BuffUuid : cur.BuffUuid,
        next.BaseId   != 0 ? next.BaseId   : cur.BaseId,
        next.Level    != 0 ? next.Level    : cur.Level,
        next.FirerId.IsNone ? cur.FirerId  : next.FirerId,
        next.Stacks   != 0 ? next.Stacks   : cur.Stacks,
        next.Layer    != 0 ? next.Layer    : cur.Layer,
        next.CreateTimeMs != 0 ? next.CreateTimeMs : cur.CreateTimeMs,
        next.DurationMs   != 0 ? next.DurationMs   : cur.DurationMs);
}
