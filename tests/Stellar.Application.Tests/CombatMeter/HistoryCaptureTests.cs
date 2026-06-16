using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Xunit;

namespace Stellar.Application.Tests.CombatMeter;

public sealed class HistoryCaptureTests
{
    [Fact]
    public void Frozen_series_are_per_source_per_channel()
    {
        var entry = new Plugin.EncounterHistoryEntry();
        var id = new EntityId(0x0000_0001_0000_0280L);
        entry.Series[id] = new SourceSeries
        {
            BucketMs = 1000,
            Dealt    = new long[] { 100, 200, 150 },
            Healing  = new long[] { 0, 0, 0 },
            Taken    = new long[] { 50, 0, 25 },
        };
        Assert.Equal(3, entry.Series[id].Dealt.Length);
        Assert.Equal(200, entry.Series[id].Dealt[1]);
        Assert.Equal(25,  entry.Series[id].Taken[2]);
    }

    [Fact]
    public void ComputeUptime_is_active_span_over_duration()
    {
        Assert.Equal(0.5f, Plugin.ComputeUptime(firstHitMs: 0, lastHitMs: 30000, durationMs: 60000));
    }

    [Fact]
    public void ComputeUptime_zero_duration_is_zero()
    {
        Assert.Equal(0f, Plugin.ComputeUptime(0, 30000, 0));
    }

    [Fact]
    public void ComputeUptime_clamps_to_one_when_span_exceeds_duration()
    {
        Assert.Equal(1f, Plugin.ComputeUptime(0, 90000, 60000));
    }

    [Fact]
    public void ComputeUptime_zero_when_no_active_span()
    {
        // lastHit <= firstHit (no progress) → 0, regardless of duration.
        Assert.Equal(0f, Plugin.ComputeUptime(5000, 5000, 60000));
    }
}
