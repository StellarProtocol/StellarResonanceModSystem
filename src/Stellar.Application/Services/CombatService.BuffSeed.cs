using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Buff storage read side + full-snapshot seeding (spec-from-talent-buffs, 2026-09-26). Reads return the
/// per-entity cached array (<see cref="EntityBuffSet"/>), rebuilt once per change.
/// </summary>
internal sealed partial class CombatService
{
    public IReadOnlyList<ActiveBuff> LocalBuffs
    {
        get
        {
            var local = _localEntityId;
            return local.IsNone ? Array.Empty<ActiveBuff>() : BuffsFor(local);
        }
    }

    public IReadOnlyList<ActiveBuff> BuffsFor(EntityId entityId)
    {
        lock (_buffsByEntityLock)
        {
            return _buffsByEntity.TryGetValue(entityId, out var set) ? set.Snapshot() : Array.Empty<ActiveBuff>();
        }
    }

    /// <summary>
    /// Seed from a FULL buff snapshot — an AOI appear (<c>SyncNearEntities</c> <c>Entity.buff_infos</c>) or the
    /// local player's <c>EnterScene</c> entity. Replaces the held set SILENTLY (no per-buff
    /// <see cref="CombatEvent.BuffChanged"/>) and raises exactly one <see cref="CombatEvent.EntityBuffsSeeded"/>
    /// carrying the complete new set — also when it is empty, so consumers can clear.
    /// <paramref name="buffs"/> null = the snapshot carried no buff list = empty set, so a re-appear can never
    /// keep stale buffs. The talent-spec state is re-derived from the snapshot (root present → that spec,
    /// no root → cleared).
    /// </summary>
    public void ReplaceEntityBuffs(EntityId entityId, IReadOnlyList<ActiveBuff>? buffs, long timestampMs)
    {
        buffs ??= Array.Empty<ActiveBuff>();
        ActiveBuff[] snapshot;
        lock (_buffsByEntityLock)
        {
            if (buffs.Count == 0)
            {
                _buffsByEntity.Remove(entityId);
                snapshot = Array.Empty<ActiveBuff>();
            }
            else
            {
                var set = new EntityBuffSet(buffs.Count);
                for (int i = 0; i < buffs.Count; i++) set.Map[buffs[i].BuffUuid] = buffs[i];
                _buffsByEntity[entityId] = set;
                snapshot = set.Snapshot();
            }
        }
        _spec.NoteSeed(entityId, snapshot, timestampMs);
        DiagBuffSeed(entityId, snapshot.Length);
        EnqueueEvent(new CombatEvent.EntityBuffsSeeded(timestampMs, entityId, snapshot));
    }

    // Shared by OnEntityDisappeared (AOI-disappear) and SweepIdleEntities (idle-TTL eviction) —
    // same buff-cache cleanup, two different triggers for "this entity is gone".
    private void RemoveEntityBuffs(EntityId entityId)
    {
        lock (_buffsByEntityLock) _buffsByEntity.Remove(entityId);
    }

    public void ClearAllBuffs()
    {
        lock (_buffsByEntityLock) _buffsByEntity.Clear();
    }
}
