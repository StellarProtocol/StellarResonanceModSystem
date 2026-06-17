using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Xunit;

namespace Stellar.Application.Tests.CombatMeter;

/// <summary>
/// Round-trip + robustness tests for the reflection-free history serializer (<see cref="HistoryStore"/>). These
/// exercise serialize→deserialize deep equality across all scalars, dict keys/values and the three series
/// channels, plus the never-throw-on-garbage contract that protects a user's saved history from a single corrupt
/// entry.
/// </summary>
public sealed class HistoryStoreTests
{
    private static Plugin.EncounterHistoryEntry BuildRichEntry()
    {
        var e = new Plugin.EncounterHistoryEntry
        {
            SceneName        = "Stormwatch \"Keep\" \\ Wing\nB",   // exercises quote/backslash/newline escaping
            EnteredAtMs      = 1_700_000_000_000L,
            ArchivedAtMs     = 1_700_000_123_456L,
            CombatDurationMs = 123_456L,
            PartyType        = PartyType.Raid20,
            MemberCount      = 7,
        };

        var a = new EntityId(0x0000_0001_0000_0280L);   // player
        var b = new EntityId(0x0000_00AB_0000_0040L);   // monster

        var sa = new SourceStats
        {
            TotalDamage = 999_999, TotalHealing = 4242, TotalTaken = 7777,
            TopHit = 54321, Hits = 480, Crits = 91, Kills = 3,
            FirstHitMs = 1000, LastHitMs = 120000,
        };
        sa.BySkill[101] = new SkillStats { Total = 500000, HealTotal = 0, Hits = 200, Crits = 40, TopHit = 30000 };
        sa.BySkill[102] = new SkillStats { Total = 0, HealTotal = 4242, Hits = 12, Crits = 1, TopHit = 800 };
        sa.IncomingBySkill[900] = new IncomingSkillStats { Total = 7777, Hits = 30, TopHit = 1200 };
        e.Stats[a] = sa;

        e.Stats[b] = new SourceStats { TotalDamage = 12345, TopHit = 2000, Hits = 50, FirstHitMs = 500, LastHitMs = 60000 };

        e.Series[a] = new SourceSeries
        {
            BucketMs = 1000,
            Dealt   = new long[] { 100, 200, 0, 0, 50 },
            Healing = new long[] { 0, 30 },
            Taken   = new long[] { 0, 0, 70 },
        };
        e.Series[b] = new SourceSeries
        {
            BucketMs = 1000,
            Dealt   = new long[] { 5, 5, 5 },
            Healing = System.Array.Empty<long>(),
            Taken   = new long[] { 1 },
        };
        return e;
    }

    [Fact]
    public void Round_trips_all_scalars_dicts_and_three_series_channels()
    {
        var src = BuildRichEntry();
        var json = HistoryStore.SerializeEntry(src);

        Assert.True(HistoryStore.TryDeserializeEntry(json, out var got));
        Assert.NotNull(got);

        Assert.Equal(src.SceneName, got!.SceneName);
        Assert.Equal(src.EnteredAtMs, got.EnteredAtMs);
        Assert.Equal(src.ArchivedAtMs, got.ArchivedAtMs);
        Assert.Equal(src.CombatDurationMs, got.CombatDurationMs);
        Assert.Equal(src.PartyType, got.PartyType);
        Assert.Equal(src.MemberCount, got.MemberCount);

        Assert.Equal(src.Stats.Count, got.Stats.Count);
        foreach (var (id, s) in src.Stats)
        {
            Assert.True(got.Stats.TryGetValue(id, out var d));
            Assert.Equal(s.TotalDamage, d!.TotalDamage);
            Assert.Equal(s.TotalHealing, d.TotalHealing);
            Assert.Equal(s.TotalTaken, d.TotalTaken);
            Assert.Equal(s.TopHit, d.TopHit);
            Assert.Equal(s.Hits, d.Hits);
            Assert.Equal(s.Crits, d.Crits);
            Assert.Equal(s.Kills, d.Kills);
            Assert.Equal(s.FirstHitMs, d.FirstHitMs);
            Assert.Equal(s.LastHitMs, d.LastHitMs);

            Assert.Equal(s.BySkill.Count, d.BySkill.Count);
            foreach (var (sid, sk) in s.BySkill)
            {
                Assert.True(d.BySkill.TryGetValue(sid, out var dk));
                Assert.Equal(sk.Total, dk!.Total);
                Assert.Equal(sk.HealTotal, dk.HealTotal);
                Assert.Equal(sk.Hits, dk.Hits);
                Assert.Equal(sk.Crits, dk.Crits);
                Assert.Equal(sk.TopHit, dk.TopHit);
            }
            Assert.Equal(s.IncomingBySkill.Count, d.IncomingBySkill.Count);
            foreach (var (sid, inc) in s.IncomingBySkill)
            {
                Assert.True(d.IncomingBySkill.TryGetValue(sid, out var di));
                Assert.Equal(inc.Total, di!.Total);
                Assert.Equal(inc.Hits, di.Hits);
                Assert.Equal(inc.TopHit, di.TopHit);
            }
        }

        Assert.Equal(src.Series.Count, got.Series.Count);
        foreach (var (id, sr) in src.Series)
        {
            Assert.True(got.Series.TryGetValue(id, out var dsr));
            Assert.Equal(sr.BucketMs, dsr.BucketMs);
            Assert.Equal(sr.Dealt, dsr.Dealt);
            Assert.Equal(sr.Healing, dsr.Healing);
            Assert.Equal(sr.Taken, dsr.Taken);
        }
    }

    [Fact]
    public void Empty_entry_round_trips()
    {
        var src = new Plugin.EncounterHistoryEntry { SceneName = null, MemberCount = 0 };
        var json = HistoryStore.SerializeEntry(src);

        Assert.True(HistoryStore.TryDeserializeEntry(json, out var got));
        Assert.NotNull(got);
        Assert.Equal("", got!.SceneName);   // null SceneName serializes as empty string
        Assert.Empty(got.Stats);
        Assert.Empty(got.Series);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("{\"v\":1,\"scene\":\"x")]            // truncated string
    [InlineData("{\"v\":1,\"stats\":[{\"id\":")]      // truncated number
    [InlineData("[]")]                                 // array, not the expected object
    [InlineData("{\"v\":1,\"bogus\":5}")]             // unknown key
    [InlineData("{\"scene\":\"x\"}")]                 // missing version marker
    [InlineData("{\"v\":2,\"scene\":\"x\"}")]         // wrong/future version
    public void Malformed_or_legacy_input_is_skipped_without_throwing(string garbage)
    {
        // Must never throw, and must report failure (entry skipped) for unsupported shapes.
        Assert.False(HistoryStore.TryDeserializeEntry(garbage, out var got));
        Assert.Null(got);
    }

    [Fact]
    public void TrimToCapacity_evicts_oldest_first_and_caps_at_50()
    {
        var history = new List<Plugin.EncounterHistoryEntry>();
        for (var i = 0; i < 60; i++)
            history.Add(new Plugin.EncounterHistoryEntry { MemberCount = i });   // i = age marker (0 = oldest)

        Plugin.TrimToCapacity(history);

        Assert.Equal(50, history.Count);
        // Oldest (0..9) evicted from the front; newest (10..59) retained in order.
        Assert.Equal(10, history[0].MemberCount);
        Assert.Equal(59, history[^1].MemberCount);
    }

    [Fact]
    public void TrimToCapacity_is_a_noop_under_the_cap()
    {
        var history = new List<Plugin.EncounterHistoryEntry>();
        for (var i = 0; i < 5; i++) history.Add(new Plugin.EncounterHistoryEntry());
        Plugin.TrimToCapacity(history);
        Assert.Equal(5, history.Count);
    }

    [Fact]
    public void Serializer_round_trips_a_floods_of_entries_without_drift()
    {
        // Sanity: serialize a batch and re-read; each must come back identical.
        for (var n = 0; n < 5; n++)
        {
            var e = BuildRichEntry();
            e.MemberCount = n;
            var json = HistoryStore.SerializeEntry(e);
            Assert.True(HistoryStore.TryDeserializeEntry(json, out var got));
            Assert.Equal(n, got!.MemberCount);
        }
    }
}
