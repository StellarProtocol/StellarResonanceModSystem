using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class SummonerMemoTests
{
    private const long Pet = (5L << 16) | 1024;
    private const long Owner = (1L << 16) | 640;
    private readonly SummonerMemo _memo = new();
    private readonly List<long> _reads = new();
    private SummonerRead? _next = new SummonerRead(0, 0);

    private SummonerRead? Read(long uuid)
    {
        _reads.Add(uuid);
        return _next;
    }

    private long Lookup(long uuid, long now) => _memo.Lookup(uuid, now, Read, out _);

    [Fact]
    public void Negative_is_trusted_inside_the_ttl_and_reread_after_it()
    {
        Assert.Equal(0, Lookup(Pet, 1_000));
        Assert.Equal(0, Lookup(Pet, 1_000 + SummonerMemo.NegativeTtlMs - 1));
        Assert.Single(_reads);
        _next = new SummonerRead(Owner, 0);
        Assert.Equal(Owner, Lookup(Pet, 1_000 + SummonerMemo.NegativeTtlMs));
        Assert.Equal(2, _reads.Count);
    }

    [Fact]
    public void Expired_negative_is_not_served_by_TryGet()
    {
        _memo.Remember(Pet, 0, 0);
        Assert.True(_memo.TryGet(Pet, SummonerMemo.NegativeTtlMs - 1, out _));
        Assert.False(_memo.TryGet(Pet, SummonerMemo.NegativeTtlMs, out _));
    }

    [Fact]
    public void Positive_replaces_and_removes_the_negative_entry()
    {
        _memo.Remember(Pet, 0, 0);
        Assert.Equal(1, _memo.NegativeCount);
        _memo.Remember(Pet, Owner, 5);
        Assert.Equal(0, _memo.NegativeCount);
        Assert.True(_memo.TryGet(Pet, 5, out var owner));
        Assert.Equal(Owner, owner);
    }

    [Fact]
    public void Positive_is_served_forever_without_rereading()
    {
        _next = new SummonerRead(Owner, 0);
        Lookup(Pet, 0);
        Assert.Equal(Owner, Lookup(Pet, 10 * SummonerMemo.NegativeTtlMs));
        Assert.Single(_reads);
    }

    [Fact]
    public void Positive_cache_clears_when_a_new_uuid_exceeds_the_cap()
    {
        for (long i = 1; i <= SummonerMemo.PositiveCapacity; i++) _memo.Remember(i, Owner, 0);
        Assert.Equal(SummonerMemo.PositiveCapacity, _memo.PositiveCount);
        _memo.Remember(1, Owner, 0);   // an existing key does not trip the cap
        Assert.Equal(SummonerMemo.PositiveCapacity, _memo.PositiveCount);
        _memo.Remember(SummonerMemo.PositiveCapacity + 1, Owner, 0);
        Assert.Equal(1, _memo.PositiveCount);
        Assert.False(_memo.TryGet(1, 0, out _));
    }

    [Fact]
    public void Negative_cache_clears_when_a_new_uuid_exceeds_the_cap()
    {
        for (long i = 1; i <= SummonerMemo.NegativeCapacity; i++) _memo.Remember(i, 0, 0);
        _memo.Remember(SummonerMemo.NegativeCapacity + 1, 0, 0);
        Assert.Equal(1, _memo.NegativeCount);
    }

    [Fact]
    public void Not_ready_caches_nothing()
    {
        _next = null;
        Assert.Equal(0, Lookup(Pet, 0));
        Assert.Equal(0, _memo.NegativeCount);
        Assert.Equal(0, _memo.PositiveCount);
        Assert.Equal(0, Lookup(Pet, 1));
        Assert.Equal(2, _reads.Count);   // asked again: nothing was condemned
    }

    [Fact]
    public void Fresh_read_is_reported_only_when_the_game_was_read()
    {
        _next = new SummonerRead(Owner, 0);
        _memo.Lookup(Pet, 0, Read, out var first);
        _memo.Lookup(Pet, 1, Read, out var second);
        Assert.Equal(new SummonerRead(Owner, 0), first);
        Assert.Null(second);
    }

    [Fact]
    public void Owner_prefers_top_then_summoner_and_never_self()
    {
        Assert.Equal(Owner, new SummonerRead(Owner, 7).OwnerOf(Pet));
        Assert.Equal(7, new SummonerRead(0, 7).OwnerOf(Pet));
        Assert.Equal(7, new SummonerRead(Pet, 7).OwnerOf(Pet));
        Assert.Equal(0, new SummonerRead(Pet, Pet).OwnerOf(Pet));
    }

    [Fact]
    public void Zero_uuid_never_reads()
    {
        Assert.Equal(0, Lookup(0, 0));
        Assert.Empty(_reads);
    }

    [Fact]
    public void Clear_forgets_positives_and_negatives()
    {
        _memo.Remember(Pet, Owner, 0);
        _memo.Remember(Pet + 1, 0, 0);
        _memo.Clear();
        Assert.Equal(0, _memo.PositiveCount);
        Assert.Equal(0, _memo.NegativeCount);
    }
}
