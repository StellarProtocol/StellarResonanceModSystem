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
    private readonly AttrReadabilityMemo _attrMemo;
    private IReadOnlyDictionary<int, long> _values = EmptyDict;
    private bool _isAvailable;
    private readonly object _lock = new();

    /// <param name="attrMemo">
    /// The shared attribute-readability memo. The same instance is handed to the
    /// Infrastructure probe by the composition root, so a subscribe here re-arms the
    /// probe over there (see <see cref="Subscribe"/> / <see cref="ClearSession"/>).
    /// </param>
    public PlayerStatsService(AttrReadabilityMemo attrMemo)
    {
        _attrMemo = attrMemo;
    }

    public bool IsAvailable => Volatile.Read(ref _isAvailable);

    public long? TryGetAttribute(int attrId)
    {
        var snap = Volatile.Read(ref _values);
        return snap.TryGetValue(attrId, out var v) ? v : null;
    }

    public void Subscribe(int attrId)
    {
        lock (_lock) { _subscribed.Add(attrId); }
        // A (re-)subscribe always re-probes: a stale "unreadable" verdict recorded while the
        // game's attribute sheet was still filling must not outlive the user's next click.
        // This is the in-game recovery — re-ticking a stat in the picker, instead of a relaunch.
        _attrMemo.Forget(attrId);
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
    /// Host OnLogout dispatcher. Also drops the attribute-readability memo so the next login probes
    /// from scratch instead of inheriting verdicts taken during the previous login window.</summary>
    internal void ClearSession()
    {
        Volatile.Write(ref _values, EmptyDict);
        Volatile.Write(ref _isAvailable, false);
        _attrMemo.Clear();
    }

    private static readonly IReadOnlyDictionary<int, long> EmptyDict
        = new Dictionary<int, long>(0);
}
