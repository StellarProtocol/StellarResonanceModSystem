using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Buff storage read side + full-snapshot seeding (spec-from-talent-buffs, 2026-09-26). Reads return the
/// per-entity cached read-only snapshot (<see cref="EntityBuffSet"/>), rebuilt once per change.
/// </summary>
internal sealed partial class CombatService
{
    // The local entity's snapshot, republished (under _buffsByEntityLock) whenever that set changes or the
    // local id is first set. LocalBuffs is read every frame on the main thread; a volatile reference read keeps
    // it from ever queueing behind the network thread, which holds the buff lock while applying deltas (and,
    // with STELLAR_DIAGNOSTICS=1, while it logs every buff).
    private volatile IReadOnlyList<ActiveBuff> _localSnapshot = EntityBuffSet.Empty;

    public IReadOnlyList<ActiveBuff> LocalBuffs => _localSnapshot;

    public IReadOnlyList<ActiveBuff> BuffsFor(EntityId entityId)
    {
        lock (_buffsByEntityLock)
        {
            return _buffsByEntity.TryGetValue(entityId, out var set) ? set.Snapshot() : EntityBuffSet.Empty;
        }
    }

    /// <summary>
    /// Seed from a FULL buff snapshot — an AOI appear (<c>SyncNearEntities</c> <c>Entity.buff_infos</c>) or the
    /// local player's <c>EnterScene</c> entity. Replaces the held set SILENTLY (no per-buff
    /// <see cref="CombatEvent.BuffChanged"/>) and raises one <see cref="CombatEvent.EntityBuffsSeeded"/> carrying
    /// the complete new set — also when it is empty but a set WAS held (so consumers can clear). Nothing held +
    /// empty snapshot (a mob/NPC appearing buff-less) raises nothing. <paramref name="buffs"/> null = the snapshot
    /// carried no buff list = empty set, so a re-appear can never keep stale buffs. The talent-spec state is
    /// re-derived from the snapshot (root present → that spec, no root → cleared).
    /// </summary>
    public void ReplaceEntityBuffs(EntityId entityId, IReadOnlyList<ActiveBuff>? buffs, long timestampMs)
    {
        buffs ??= Array.Empty<ActiveBuff>();
        IReadOnlyList<ActiveBuff> snapshot;
        bool announce;
        lock (_buffsByEntityLock)
        {
            if (buffs.Count == 0)
            {
                announce = _buffsByEntity.Remove(entityId);
                snapshot = EntityBuffSet.Empty;
            }
            else
            {
                var set = new EntityBuffSet(buffs.Count);
                for (int i = 0; i < buffs.Count; i++) set.Map[buffs[i].BuffUuid] = buffs[i];
                _buffsByEntity[entityId] = set;
                snapshot = set.Snapshot();
                announce = true;
            }
            if (entityId == _localEntityId) _localSnapshot = snapshot;
        }
        _spec.NoteSeed(entityId, snapshot, timestampMs);
        if (!announce) return;
        DiagBuffSeed(entityId, snapshot.Count);
        EnqueueEvent(new CombatEvent.EntityBuffsSeeded(timestampMs, entityId, snapshot));
    }

    // Caller holds _buffsByEntityLock. Republish the local snapshot after the local set changed.
    private void PublishLocalSnapshot(EntityId entityId, EntityBuffSet? set)
    {
        if (entityId == _localEntityId) _localSnapshot = set?.Snapshot() ?? EntityBuffSet.Empty;
    }

    // Shared by OnEntityDisappeared (AOI-disappear) and SweepIdleEntities (idle-TTL eviction) —
    // same buff-cache cleanup, two different triggers for "this entity is gone".
    private void RemoveEntityBuffs(EntityId entityId)
    {
        lock (_buffsByEntityLock)
        {
            _buffsByEntity.Remove(entityId);
            PublishLocalSnapshot(entityId, null);
        }
    }

    public void ClearAllBuffs()
    {
        lock (_buffsByEntityLock)
        {
            _buffsByEntity.Clear();
            _localSnapshot = EntityBuffSet.Empty;
        }
    }
}
