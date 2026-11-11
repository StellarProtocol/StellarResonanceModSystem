using Stellar.Infrastructure.Unity;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>Raid profile 2026-11-10: the per-frame hover hit-test pass was 2.9% of the main thread with the mouse at rest.
/// The gate skips it while nothing under the pointer can have changed, but must never leave a hover stale for long.</summary>
public sealed class HoverScanGateTests
{
    private const float Frame = 1f / 100f;

    [Fact]
    public void FirstFrameAlwaysScans() => Assert.True(new HoverScanGate().ShouldScan(10, 10, 5f));

    [Fact]
    public void StillMouseSkipsUntilTheSafetyNet()
    {
        var g = new HoverScanGate();
        Assert.True(g.ShouldScan(10, 10, 0f));
        Assert.False(g.ShouldScan(10, 10, Frame));
        Assert.False(g.ShouldScan(10, 10, HoverScanGate.IdleRescanSec - Frame));
        Assert.True(g.ShouldScan(10, 10, HoverScanGate.IdleRescanSec));
        Assert.False(g.ShouldScan(10, 10, HoverScanGate.IdleRescanSec + Frame));
    }

    [Fact]
    public void MouseMoveScansImmediately()
    {
        var g = new HoverScanGate();
        g.ShouldScan(10, 10, 0f);
        Assert.True(g.ShouldScan(11, 10, Frame));
        Assert.False(g.ShouldScan(11, 10, 2 * Frame));
    }

    [Fact]
    public void DirtyScansThisFrameAndTheNext()
    {
        var g = new HoverScanGate();
        g.ShouldScan(10, 10, 0f);
        g.MarkDirty();
        Assert.True(g.ShouldScan(10, 10, Frame));
        Assert.True(g.ShouldScan(10, 10, 2 * Frame));    // uGUI lays the change out only at the end of the dirty frame
        Assert.False(g.ShouldScan(10, 10, 3 * Frame));
    }

    [Fact]
    public void InputKeepsScanningForTheInputWindow()
    {
        var g = new HoverScanGate();
        g.ShouldScan(10, 10, 0f);
        g.MarkInput(1f);
        Assert.True(g.ShouldScan(10, 10, 1f));
        Assert.True(g.ShouldScan(10, 10, 1f + HoverScanGate.AfterInputSec - Frame));
        Assert.False(g.ShouldScan(10, 10, 1f + HoverScanGate.AfterInputSec + Frame));
    }

    [Fact]
    public void ClockGoingBackwardsRescans()
    {
        var g = new HoverScanGate();
        g.ShouldScan(10, 10, 100f);
        Assert.True(g.ShouldScan(10, 10, 1f));
    }

    [Fact]
    public void HiddenCursorClearsOnceThenRescansWhenItReturns()
    {
        var g = new HoverScanGate();
        g.ShouldScan(10, 10, 0f);
        Assert.True(g.CursorHidden());    // first hidden frame: caller turns lit cells off
        Assert.False(g.CursorHidden());   // later hidden frames: nothing to do
        Assert.False(g.CursorHidden());
        Assert.True(g.ShouldScan(10, 10, Frame));    // cursor back at the same spot, inside the safety net: still scans
        Assert.False(g.ShouldScan(10, 10, 2 * Frame));
        Assert.True(g.CursorHidden());    // hiding again clears again
    }

    [Fact]
    public void CountsScansAndSkips()
    {
        var g = new HoverScanGate();
        g.ShouldScan(10, 10, 0f);
        g.ShouldScan(10, 10, Frame);
        g.CursorHidden(); g.CursorHidden();
        Assert.Equal((1, 1, 2), g.TakeCounts());
        Assert.Equal((0, 0, 0), g.TakeCounts());
    }
}
