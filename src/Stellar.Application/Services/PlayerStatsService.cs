// src/Stellar.Application/Services/PlayerStatsService.cs
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

internal sealed class PlayerStatsService : IPlayerStats
{
    private readonly HashSet<int> _subscribed = new();
    private IReadOnlyDictionary<int, long> _values = EmptyDict;
    private bool _isAvailable;
    private readonly object _lock = new();

    public bool IsAvailable => Volatile.Read(ref _isAvailable);

    public long? TryGetAttribute(int attrId)
    {
        var snap = Volatile.Read(ref _values);
        return snap.TryGetValue(attrId, out var v) ? v : null;
    }

    public void Subscribe(int attrId)
    {
        lock (_lock) { _subscribed.Add(attrId); }
    }

    public void Unsubscribe(int attrId)
    {
        lock (_lock) { _subscribed.Remove(attrId); }
    }

    /// <summary>
    /// Called from the Host's per-tick dispatcher. Snapshots the subscription
    /// set under the lock, samples via the probe, atomic-swaps the values dict.
    /// </summary>
    internal void Refresh(IPlayerStatsProbe probe)
    {
        int[] snap;
        lock (_lock) { snap = _subscribed.ToArray(); }

        if (probe.TrySample(snap, out var values))
        {
            Volatile.Write(ref _values, values);
            Volatile.Write(ref _isAvailable, true);
        }
        else
        {
            Volatile.Write(ref _isAvailable, false);
        }
    }

    /// <summary>Clear account/character-scoped stat values on logout. Leaves the plugin subscription
    /// set (<see cref="_subscribed"/>) intact — that's plugin state, not account data. Called by the
    /// Host OnLogout dispatcher.</summary>
    internal void ClearSession()
    {
        Volatile.Write(ref _values, EmptyDict);
        Volatile.Write(ref _isAvailable, false);
    }

    private static readonly IReadOnlyDictionary<int, long> EmptyDict
        = new Dictionary<int, long>(0);
}
