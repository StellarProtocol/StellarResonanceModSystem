using Stellar.Application.Abstractions;
using Xunit;

namespace Stellar.Application.Tests.Photo;

public sealed class NegativeProbeCacheTests
{
    private sealed class FakeClock
    {
        public long Ms;
        public long Now() => Ms;
    }

    [Fact]
    public void Never_marked_negative_is_never_suppressed()
    {
        var clock = new FakeClock();
        var cache = new NegativeProbeCache(clock.Now, ttlMs: 5_000);
        Assert.False(cache.IsSuppressed);
    }

    [Fact]
    public void Marking_negative_suppresses_until_the_ttl_elapses_then_allows_a_reprobe()
    {
        var clock = new FakeClock();
        var cache = new NegativeProbeCache(clock.Now, ttlMs: 5_000);

        cache.MarkNegative();
        Assert.True(cache.IsSuppressed);

        clock.Ms = 4_999;
        Assert.True(cache.IsSuppressed); // still within the TTL

        clock.Ms = 5_000;
        Assert.False(cache.IsSuppressed); // TTL elapsed — the next probe is allowed to try again
    }

    [Fact]
    public void MarkNegative_returns_true_only_the_first_time_since_construction()
    {
        var clock = new FakeClock();
        var cache = new NegativeProbeCache(clock.Now, ttlMs: 5_000);

        Assert.True(cache.MarkNegative());  // first time — caller logs its one Warning
        Assert.False(cache.MarkNegative()); // still the same unresolved incident — no second Warning
    }

    [Fact]
    public void MarkRecovered_returns_false_on_a_first_ever_successful_resolution()
    {
        var clock = new FakeClock();
        var cache = new NegativeProbeCache(clock.Now, ttlMs: 5_000);

        Assert.False(cache.MarkRecovered()); // never negative — nothing to recover FROM, no Info log
    }

    [Fact]
    public void MarkRecovered_returns_true_exactly_once_after_a_warned_negative()
    {
        var clock = new FakeClock();
        var cache = new NegativeProbeCache(clock.Now, ttlMs: 5_000);

        cache.MarkNegative();
        Assert.True(cache.MarkRecovered());  // recovered from a warned negative — caller logs its one Info
        Assert.False(cache.MarkRecovered()); // already recovered — no repeat Info
    }

    [Fact]
    public void Reset_clears_suppression_immediately_even_before_the_ttl_elapses()
    {
        var clock = new FakeClock();
        var cache = new NegativeProbeCache(clock.Now, ttlMs: 5_000);

        cache.MarkNegative();
        Assert.True(cache.IsSuppressed);

        cache.Reset(); // e.g. a scene change / hot-update-ready signal
        Assert.False(cache.IsSuppressed);
    }

    [Fact]
    public void Reset_rearms_the_warn_once_flag_so_a_fresh_incident_warns_again()
    {
        var clock = new FakeClock();
        var cache = new NegativeProbeCache(clock.Now, ttlMs: 5_000);

        Assert.True(cache.MarkNegative());
        cache.Reset();
        Assert.True(cache.MarkNegative()); // a NEW incident after the reset gets its own one-time Warning
    }
}
