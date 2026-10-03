using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Spec § 7 budget: position hold < 0.3 ms/frame (50 characters) — over budget ⇒ hold off.
public sealed class HoldBudgetTests
{
    [Fact]
    public void Under_budget_never_trips()
    {
        var b = new HoldBudget();
        for (var i = 0; i < 600; i++) Assert.False(b.Record(0.20));
        Assert.False(b.Exceeded);
    }

    [Fact]
    public void A_window_averaging_over_0_30_ms_trips_and_stays_tripped()
    {
        var b = new HoldBudget();
        var tripped = false;
        for (var i = 0; i < HoldBudget.Window; i++) tripped = b.Record(0.35);
        Assert.True(tripped);
        Assert.True(b.Exceeded);
        Assert.InRange(b.LastAverageMs, 0.349, 0.351);
        Assert.True(b.Record(0.01));
    }

    [Fact]
    public void One_spike_inside_a_cheap_window_does_not_trip()
    {
        var b = new HoldBudget();
        b.Record(3.0);
        for (var i = 1; i < HoldBudget.Window; i++) b.Record(0.2);
        Assert.False(b.Exceeded);   // (3.0 + 59 × 0.2) / 60 = 0.2467
    }

    [Fact]
    public void Reset_clears_a_trip()
    {
        var b = new HoldBudget();
        for (var i = 0; i < HoldBudget.Window; i++) b.Record(1.0);
        b.Reset();
        Assert.False(b.Exceeded);
    }
}
