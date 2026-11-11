using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Per-buff-instance effect ids (<c>ICombatBuffEffectLookup</c>). A side table, NOT a field on
/// <see cref="ActiveBuff"/>: adding a positional record parameter would break every compiled caller
/// (PerPluginServices-Decoration.md §3), and the ids change on Customize events that never touch the buff set.
/// Keyed by (entId = uuid &gt;&gt; 16, BuffUuid) — the local player's entity uuid carries the isClient bit
/// (Threat-Aggro-Tracking.md) that a wire HostUuid may not, so only the entId part is a stable identity. Written on
/// the wire thread, read by plugins on the main thread → own lock (never the buff lock, so a reader never queues
/// behind a buff delta being applied). Mirrors the Mechanic Callouts McBuffEffectPatch table it replaces.
/// </summary>
internal sealed partial class CombatService
{
    // Safety valve, same as McBuffEffectPatch: entries are dropped on remove/scene change, and a flushed one is
    // re-sent with the next sync/customize. Only buffs that actually carry ids are stored, so this is rarely reached.
    internal const int MaxBuffEffectIdEntries = 4096;

    private readonly Dictionary<(long Ent, int Buff), int[]> _buffEffectIds = new();
    private readonly object _buffEffectIdsLock = new();

    public void ApplyBuffEffectIds(EntityId entityId, IReadOnlyList<BuffEffectIdUpdate> updates)
    {
        lock (_buffEffectIdsLock)
        {
            for (int i = 0; i < updates.Count; i++)
            {
                var u = updates[i];
                long ent = (u.HostUuid != 0 ? u.HostUuid : entityId.Value) >> 16;
                if (ent == 0 || u.BuffUuid == 0) continue;
                var key = (ent, u.BuffUuid);
                if (u.EffectIds is null) { _buffEffectIds.Remove(key); continue; }
                if (_buffEffectIds.Count > MaxBuffEffectIdEntries) _buffEffectIds.Clear();
                _buffEffectIds[key] = u.EffectIds;
            }
        }
    }

    public bool TryGetBuffEffectIds(EntityId entityId, int buffUuid, out IReadOnlyList<int> effectIds)
    {
        lock (_buffEffectIdsLock)
        {
            if (_buffEffectIds.TryGetValue((entityId.Value >> 16, buffUuid), out var ids))
            {
                effectIds = ids;
                return true;
            }
        }
        effectIds = Array.Empty<int>();
        return false;
    }

    // Scene change / logout (from ClearAllBuffs): buff uuids are recycled across scenes (Buff-Tracking.md §8).
    private void ClearBuffEffectIds()
    {
        lock (_buffEffectIdsLock) _buffEffectIds.Clear();
    }
}
