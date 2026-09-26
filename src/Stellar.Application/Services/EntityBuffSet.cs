using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// One entity's live buff set (keyed by buff uuid) plus a cached array snapshot, rebuilt only after the set
/// changed — the version-stamp pattern <c>CombatService.LocalCooldowns</c> uses — so <c>BuffsFor</c> /
/// <c>LocalBuffs</c> reads allocate once per change instead of once per read. A handed-out snapshot is never
/// mutated. Not thread-safe: callers hold <c>CombatService._buffsByEntityLock</c>.
/// </summary>
internal sealed class EntityBuffSet
{
    private int _version;
    private int _snapshotVersion = -1;
    private ActiveBuff[] _snapshot = Array.Empty<ActiveBuff>();

    public EntityBuffSet(int capacity)
    {
        Map = new Dictionary<int, ActiveBuff>(capacity);
    }

    public Dictionary<int, ActiveBuff> Map { get; }

    /// <summary>Mark the cached snapshot stale after a mutation of <see cref="Map"/>.</summary>
    public void Invalidate() => _version++;

    /// <summary>The current set as an array (cached until the next <see cref="Invalidate"/>).</summary>
    public ActiveBuff[] Snapshot()
    {
        if (_snapshotVersion == _version) return _snapshot;
        var arr = Map.Count == 0 ? Array.Empty<ActiveBuff>() : new ActiveBuff[Map.Count];
        Map.Values.CopyTo(arr, 0);
        _snapshot = arr;
        _snapshotVersion = _version;
        return _snapshot;
    }
}
