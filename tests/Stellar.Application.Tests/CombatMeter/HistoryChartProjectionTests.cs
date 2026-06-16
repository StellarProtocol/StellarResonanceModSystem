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

}
