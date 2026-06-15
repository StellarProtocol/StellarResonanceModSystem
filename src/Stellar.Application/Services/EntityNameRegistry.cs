using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Display-name cache keyed by entity. Populated from AttrName
/// (<c>EAttrType=1</c>) observations on AoiSyncDelta; read by plugins
/// (e.g. CombatMeter) to render a human label instead of <c>Player#&lt;uid&gt;</c>.
/// Writers run on the network receive thread, readers on the Unity main
/// thread, so the dictionary is guarded by a dedicated lock.
/// </summary>
internal sealed class EntityNameRegistry
{
    private readonly Dictionary<EntityId, string> _names = new();
    private readonly object _lock = new();

    /// <summary>
    /// Returns the cached name for <paramref name="entityId"/>, or <c>null</c>
    /// when no name has been observed yet.
    /// </summary>
    public string? Get(EntityId entityId)
    {
        lock (_lock)
        {
            return _names.TryGetValue(entityId, out var name) ? name : null;
        }
    }

    /// <summary>
    /// Set / overwrite the cached name. Null or empty inputs are silently
    /// ignored so a transient AttrName row carrying an empty string can't
    /// clobber a previously resolved name. Identical-name writes are
    /// short-circuited to avoid a needless dictionary assignment under lock.
    /// </summary>
    public void Set(EntityId entityId, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_lock)
        {
            if (_names.TryGetValue(entityId, out var existing) && existing == name) return;
            _names[entityId] = name;
        }
    }

    /// <summary>
    /// Remove the cached name for <paramref name="entityId"/>. No-op when
    /// the id is unknown.
    /// </summary>
    public void Evict(EntityId entityId)
    {
        lock (_lock)
        {
            _names.Remove(entityId);
        }
    }
}
