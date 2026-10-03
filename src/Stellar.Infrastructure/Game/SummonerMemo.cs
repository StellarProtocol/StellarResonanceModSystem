using System;
using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>What one game read of an entity's summoner returned: <c>ZEntity.TopSummonUuid</c> and <c>SummonUuid</c>
/// (0 when absent / the entity is gone).</summary>
internal readonly record struct SummonerRead(long Top, long Summoner)
{
    /// <summary>The owner to report for <paramref name="uuid"/>: top summoner, else direct summoner, never the entity
    /// itself; 0 when neither is usable.</summary>
    public long OwnerOf(long uuid) => Top != 0 && Top != uuid ? Top : Summoner != uuid ? Summoner : 0;
}

/// <summary>Pure memo + policy for <see cref="GameSummonerLookup"/> (Task 10): positives kept up to
/// <see cref="PositiveCapacity"/> (cleared when exceeded), negatives trusted for <see cref="NegativeTtlMs"/> then
/// re-read (docs/il2cpp-probing-safety.md rule 3 — a summon whose attrs arrive late must be retried), bounded the same
/// way; a client-wide "not ready" read (null) caches nothing (rule 1). The clock is a parameter, so it is unit-tested
/// with plain numbers. No reflection, no IL2CPP. Single-threaded (main thread).</summary>
internal sealed class SummonerMemo
{
    internal const int PositiveCapacity = 1024;
    internal const int NegativeCapacity = 1024;
    internal const long NegativeTtlMs = 10_000;
    private readonly Dictionary<long, long> _owners = new();
    private readonly Dictionary<long, long> _missedAtMs = new();

    public int PositiveCount => _owners.Count;
    public int NegativeCount => _missedAtMs.Count;

    /// <summary>True when a still-trusted answer exists: a remembered owner, or 0 for an unexpired miss.</summary>
    public bool TryGet(long uuid, long nowMs, out long owner)
    {
        if (_owners.TryGetValue(uuid, out owner)) return true;
        owner = 0;
        return _missedAtMs.TryGetValue(uuid, out var at) && nowMs - at < NegativeTtlMs;
    }

    /// <summary>Records an answer: a non-zero owner replaces (and removes) any negative entry; 0 (re)stamps the miss.</summary>
    public void Remember(long uuid, long owner, long nowMs)
    {
        if (owner != 0)
        {
            if (_owners.Count >= PositiveCapacity && !_owners.ContainsKey(uuid)) _owners.Clear();
            _owners[uuid] = owner;
            _missedAtMs.Remove(uuid);
            return;
        }
        if (_missedAtMs.Count >= NegativeCapacity && !_missedAtMs.ContainsKey(uuid)) _missedAtMs.Clear();
        _missedAtMs[uuid] = nowMs;
    }

    /// <summary>The whole lookup policy. <paramref name="read"/> returns null when the game side is not ready
    /// client-wide (types / manager missing) — then nothing is cached and 0 is returned. <paramref name="fresh"/> is
    /// the read this call made (null when it was served from the memo or the source was not ready).</summary>
    public long Lookup(long uuid, long nowMs, Func<long, SummonerRead?> read, out SummonerRead? fresh)
    {
        fresh = null;
        if (uuid == 0) return 0;
        if (TryGet(uuid, nowMs, out var known)) return known;
        if (read(uuid) is not { } r) return 0;
        fresh = r;
        var owner = r.OwnerOf(uuid);
        Remember(uuid, owner, nowMs);
        return owner;
    }

    public void Clear()
    {
        _owners.Clear();
        _missedAtMs.Clear();
    }
}
