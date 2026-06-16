using Stellar.Abstractions.Domain;
using Stellar.CombatMeter;
using Xunit;

namespace Stellar.Application.Tests.CombatMeter;

/// <summary>
/// Pure projection logic behind the history timeline chart + metric-aware contribution table:
/// metric→aggregate selection, column labels, and per-bucket team-total summation across sources.
/// All exercised without a Unity host (the CombatMeter csproj grants InternalsVisibleTo).
/// </summary>
public sealed class HistoryChartProjectionTests
{
    [Fact]
    public void MetricValue_picks_the_right_aggregate()
    {
        var s = new SourceStats { TotalDamage = 1000, TotalHealing = 400, TotalTaken = 250 };
        Assert.Equal(1000, Plugin.MetricValueOf(s, Metric.Dps));
        Assert.Equal(400,  Plugin.MetricValueOf(s, Metric.Hps));
        Assert.Equal(250,  Plugin.MetricValueOf(s, Metric.Taken));
    }

    [Fact]
    public void MetricColumnLabel_matches_metric()
    {
        Assert.Equal("DMG",   Plugin.MetricColumnLabel(Metric.Dps));
        Assert.Equal("HEAL",  Plugin.MetricColumnLabel(Metric.Hps));
        Assert.Equal("TAKEN", Plugin.MetricColumnLabel(Metric.Taken));
    }

    [Fact]
    public void TeamTotal_sums_channel_across_sources_per_bucket()
    {
        var entry = new Plugin.EncounterHistoryEntry();
        entry.Series[new EntityId(1)] = new SourceSeries
        {
            BucketMs = 1000, Dealt = new long[] { 10, 20 }, Healing = new long[0], Taken = new long[0],
        };
        entry.Series[new EntityId(2)] = new SourceSeries
        {
            BucketMs = 1000, Dealt = new long[] { 5, 5, 5 }, Healing = new long[0], Taken = new long[0],
        };

        var total = Plugin.TeamTotalSeries(entry, Metric.Dps);

        Assert.Equal(new long[] { 15, 25, 5 }, total);
    }
}
