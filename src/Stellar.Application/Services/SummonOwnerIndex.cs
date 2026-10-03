using System.Collections.Generic;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Services;

/// <summary>Summon → summoner links from <see cref="CombatEvent.EntitySummonAppeared"/> (the wire's TopSummonerId /
/// SummonerId), so a pet's or Battle Imagine's effect resolves to the player who owns it. Bounded: at
/// <see cref="Capacity"/> distinct summons the map starts over. Thread-safe (events may arrive off the main thread).</summary>
internal sealed class SummonOwnerIndex
{
    public const int Capacity = 4096;
    private const int MaxHops = 4;
    private readonly object _gate = new();
    private readonly Dictionary<long, long> _owner = new();

    public void OnCombatEvent(CombatEvent e)
    {
        if (e is CombatEvent.EntitySummonAppeared s) Record(s.SummonerId, s.SummonId);
    }

    public void Record(EntityId summoner, EntityId summon)
    {
        if (summoner.IsNone || summon.IsNone || summoner == summon) return;
        lock (_gate)
        {
            if (_owner.Count >= Capacity && !_owner.ContainsKey(summon.Value)) _owner.Clear();
            _owner[summon.Value] = summoner.Value;
        }
    }

    /// <summary>The top owner of <paramref name="uuid"/> (itself when it has no recorded owner), following at most
    /// <see cref="MaxHops"/> links so a bad cycle cannot loop.</summary>
    public long TopOwner(long uuid)
    {
        lock (_gate)
        {
            var cur = uuid;
            for (var i = 0; i < MaxHops && _owner.TryGetValue(cur, out var next); i++) cur = next;
            return cur;
        }
    }

    public void Clear()
    {
        lock (_gate) _owner.Clear();
    }
}
