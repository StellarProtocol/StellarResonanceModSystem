using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

public sealed class RateGateTests
{
    [Fact]
    public void Crossed_is_true_once_per_interval()
    {
        var gate = new RateGate();
        // 30 Hz -> interval ~33.3ms. Feed 10ms beats; should cross on the 4th (40ms >= 33.3ms).
        Assert.False(gate.Crossed(0.010f, 30));
        Assert.False(gate.Crossed(0.010f, 30));
        Assert.False(gate.Crossed(0.010f, 30));
        Assert.True(gate.Crossed(0.010f, 30));
    }

    [Fact]
    public void LastDt_accumulates_real_time_between_crossings()
    {
        var gate = new RateGate();
        gate.Crossed(0.010f, 30);
        gate.Crossed(0.010f, 30);
        gate.Crossed(0.010f, 30);
        Assert.True(gate.Crossed(0.010f, 30));
        Assert.Equal(0.040f, gate.LastDt, 3);   // 4 x 10ms accumulated since last cross
    }

    [Fact]
    public void Fast_consumer_crosses_every_beat()
    {
        var gate = new RateGate();
        // 144 Hz interval ~6.9ms; a 7ms beat crosses each time.
        Assert.True(gate.Crossed(0.0070f, 144));
        Assert.True(gate.Crossed(0.0070f, 144));
    }

    [Fact]
    public void A_long_hitch_does_not_cause_runaway_catch_up()
    {
        var gate = new RateGate();
        Assert.True(gate.Crossed(5.0f, 30));   // 5s hitch -> crosses once
        Assert.False(gate.Crossed(0.001f, 30)); // residue clamped: next tiny beat does NOT immediately re-cross
    }
}
