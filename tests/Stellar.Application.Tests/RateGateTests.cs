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

    [Fact]
    public void LastDt_resets_and_reaccumulates_after_crossing()
    {
        var gate = new RateGate();
        // Drive to the first crossing (4 x 10ms at 30Hz).
        for (var i = 0; i < 4; i++) gate.Crossed(0.010f, 30);
        // _elapsed resets to 0; the residual ~6.7ms carries in _acc.
        gate.Crossed(0.010f, 30);            // acc ~16.7ms -> no cross
        gate.Crossed(0.010f, 30);            // acc ~26.7ms -> no cross
        Assert.True(gate.Crossed(0.010f, 30)); // acc ~36.7ms >= 33.3ms -> cross
        // precision:3 == +/-0.5ms tolerance; float32 accumulation error here is ~nanoseconds.
        Assert.Equal(0.030f, gate.LastDt, 3);
    }
}
