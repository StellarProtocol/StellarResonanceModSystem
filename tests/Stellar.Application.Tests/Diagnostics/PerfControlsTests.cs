using Stellar.Abstractions.Diagnostics;
using Xunit;

namespace Stellar.Application.Tests.Diagnostics;

[Collection("PerfStaticState")]
public sealed class PerfControlsTests
{
    [Fact]
    public void Defaults_AreAllInert()
    {
        PerfControls.ResetForTests();
        Assert.False(PerfControls.MasterHudKill);
        Assert.False(PerfControls.ChromeKill);
        Assert.False(PerfControls.ForceOpaque);
        Assert.False(PerfControls.IsMuted("anything"));
        Assert.False(PerfControls.IsThrottledOut(0));
    }

    [Fact]
    public void Mute_TogglesPerWindow()
    {
        PerfControls.ResetForTests();
        PerfControls.SetMuted("party", true);
        Assert.True(PerfControls.IsMuted("party"));
        Assert.False(PerfControls.IsMuted("tracker"));
        PerfControls.SetMuted("party", false);
        Assert.False(PerfControls.IsMuted("party"));
    }

    [Fact]
    public void ThrottleN1_NeverSkips()
    {
        PerfControls.ResetForTests();
        PerfControls.ThrottleN = 1;
        Assert.False(PerfControls.IsThrottledOut(0));
        Assert.False(PerfControls.IsThrottledOut(1));
        Assert.False(PerfControls.IsThrottledOut(7));
    }

    [Fact]
    public void ThrottleN3_DrawsEveryThirdFrame()
    {
        PerfControls.ResetForTests();
        PerfControls.ThrottleN = 3;
        Assert.False(PerfControls.IsThrottledOut(0));  // 0 % 3 == 0 => draw
        Assert.True(PerfControls.IsThrottledOut(1));   // skip
        Assert.True(PerfControls.IsThrottledOut(2));   // skip
        Assert.False(PerfControls.IsThrottledOut(3));  // draw
    }
}
