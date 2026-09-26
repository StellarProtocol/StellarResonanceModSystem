using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Buff event accumulation. AoiSyncDelta field 10 (BuffEffectSync) is an EVENT
/// stream — each delta carries only the buffs added/refreshed/removed this tick,
/// keyed by BuffUuid. We maintain a per-entity buff set and emit per-buff BuffChanged
/// events. Full snapshots (appear seeds) and the read side live in CombatService.BuffSeed.cs.
/// </summary>
internal sealed partial class CombatService
{
    public void ApplyBuffEvents(
        EntityId entityId,
        IReadOnlyList<ActiveBuff> upserts,
        IReadOnlyList<int> removedBuffUuids,
        long timestampMs)
    {
        // Touch: buff-only-refreshed entities must not be swept as idle (Task 3 idle sweep).
        _entities.Touch(entityId, System.Environment.TickCount64);
        lock (_buffsByEntityLock)
        {
            if (!_buffsByEntity.TryGetValue(entityId, out var set))
            {
                set = new EntityBuffSet(upserts.Count);
                _buffsByEntity[entityId] = set;
            }

            bool changed = ApplyUpserts(entityId, set.Map, upserts, timestampMs);
            changed |= ApplyRemovals(entityId, set.Map, removedBuffUuids, timestampMs);
            if (!changed) return;
            set.Invalidate();
            PublishLocalSnapshot(entityId, set);
        }
    }

    // Caller holds _buffsByEntityLock. Returns whether any buff was added/refreshed.
    private bool ApplyUpserts(EntityId entityId, Dictionary<int, ActiveBuff> set,
        IReadOnlyList<ActiveBuff> upserts, long timestampMs)
    {
        bool changed = false;
        for (int i = 0; i < upserts.Count; i++)
        {
            var b = upserts[i];
            if (set.TryGetValue(b.BuffUuid, out var prev))
            {
                var merged = MergeNonZero(prev, b);
                if (merged.Equals(prev)) continue;   // no-op refresh — emit nothing
                set[b.BuffUuid] = merged;
                _spec.NoteBuff(entityId, merged.BaseId, merged.SourceKind, timestampMs);
                DiagBuffChange("refreshed", entityId, merged, timestampMs);
                EnqueueEvent(new CombatEvent.BuffChanged(
                    timestampMs, entityId, merged.BuffUuid, merged.BaseId,
                    BuffChangeKind.Refreshed, merged.Stacks, merged.Layer, merged.DurationMs,
                    merged.FirerId, merged.SourceKind, merged.SourceId));
            }
            else
            {
                set[b.BuffUuid] = b;
                _spec.NoteBuff(entityId, b.BaseId, b.SourceKind, timestampMs);
                DiagBuffChange("applied", entityId, b, timestampMs);
                EnqueueEvent(new CombatEvent.BuffChanged(
                    timestampMs, entityId, b.BuffUuid, b.BaseId,
                    BuffChangeKind.Applied, b.Stacks, b.Layer, b.DurationMs,
                    b.FirerId, b.SourceKind, b.SourceId));
            }
            changed = true;
        }
        return changed;
    }

    // Caller holds _buffsByEntityLock. Returns whether any buff was removed.
    private bool ApplyRemovals(EntityId entityId, Dictionary<int, ActiveBuff> set,
        IReadOnlyList<int> removedBuffUuids, long timestampMs)
    {
        bool changed = false;
        for (int i = 0; i < removedBuffUuids.Count; i++)
        {
            int uuid = removedBuffUuids[i];
            if (set.Remove(uuid, out var old))
            {
                _spec.NoteBuffRemoved(entityId, old.BaseId, old.SourceKind);
                DiagBuffChange("removed", entityId, old, timestampMs);
                EnqueueEvent(new CombatEvent.BuffChanged(
                    timestampMs, entityId, old.BuffUuid, old.BaseId,
                    BuffChangeKind.Removed, old.Stacks, old.Layer, old.DurationMs,
                    old.FirerId, old.SourceKind, old.SourceId));
                changed = true;
            }
        }
        return changed;
    }

    // Overwrite cur with next's non-default scalar fields. Partial BuffChange
    // upserts (BaseId=0; only layer/duration/createtime) merge onto the existing
    // entry so they never clobber the real BaseId/CreateTime set at add time.
    //
    // FightSourceInfo is merged as a pair: a partial BuffChange upsert carries
    // (0,0) and must not clobber; a full BuffInfo may legitimately carry kind 0
    // = Skill. SourceKind==0 does NOT mean "absent" (Skill is EFightSource's
    // most common value), so the two fields cannot be defaulted independently —
    // doing so let a partial upsert's absent kind (0) survive merge next to a
    // full upsert's real id, producing a (kind, id) pair never seen on the wire.
    private static ActiveBuff MergeNonZero(ActiveBuff cur, ActiveBuff next)
    {
        bool nextHasSource = next.SourceKind != 0 || next.SourceId != 0;
        return new ActiveBuff(
            next.BuffUuid != 0 ? next.BuffUuid : cur.BuffUuid,
            next.BaseId   != 0 ? next.BaseId   : cur.BaseId,
            next.Level    != 0 ? next.Level    : cur.Level,
            next.FirerId.IsNone ? cur.FirerId  : next.FirerId,
            next.Stacks   != 0 ? next.Stacks   : cur.Stacks,
            next.Layer    != 0 ? next.Layer    : cur.Layer,
            next.CreateTimeMs != 0 ? next.CreateTimeMs : cur.CreateTimeMs,
            next.DurationMs   != 0 ? next.DurationMs   : cur.DurationMs,
            nextHasSource ? next.SourceKind : cur.SourceKind,
            nextHasSource ? next.SourceId   : cur.SourceId);
    }
}
