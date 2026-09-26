using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// One entity's live buff set (keyed by buff uuid) plus a cached READ-ONLY snapshot, rebuilt only after the set
/// changed — the version-stamp pattern <c>CombatService.LocalCooldowns</c> uses — so <c>BuffsFor</c> /
/// <c>LocalBuffs</c> / the <c>EntityBuffsSeeded</c> payload allocate once per change instead of once per read.
/// The snapshot is a <see cref="ReadOnlyCollection{T}"/> over a private array: the three consumers share it, and
/// none can cast it back to <c>ActiveBuff[]</c> and write into what the others see. Not thread-safe: callers hold
/// <c>CombatService._buffsByEntityLock</c>.
/// </summary>
internal sealed class EntityBuffSet
{
    private int _version;
    private int _snapshotVersion = -1;
    private IReadOnlyList<ActiveBuff> _snapshot = Empty;

    /// <summary>The shared empty read-only snapshot.</summary>
    public static readonly IReadOnlyList<ActiveBuff> Empty = Array.AsReadOnly(Array.Empty<ActiveBuff>());

    public EntityBuffSet(int capacity)
    {
        Map = new Dictionary<int, ActiveBuff>(capacity);
    }

    public Dictionary<int, ActiveBuff> Map { get; }

    /// <summary>Mark the cached snapshot stale after a mutation of <see cref="Map"/>.</summary>
    public void Invalidate() => _version++;

    /// <summary>The current set, read-only (cached until the next <see cref="Invalidate"/>).</summary>
    public IReadOnlyList<ActiveBuff> Snapshot()
    {
        if (_snapshotVersion == _version) return _snapshot;
        if (Map.Count == 0)
        {
            _snapshot = Empty;
        }
        else
        {
            var arr = new ActiveBuff[Map.Count];
            Map.Values.CopyTo(arr, 0);
            _snapshot = Array.AsReadOnly(arr);
        }
        _snapshotVersion = _version;
        return _snapshot;
    }
}
