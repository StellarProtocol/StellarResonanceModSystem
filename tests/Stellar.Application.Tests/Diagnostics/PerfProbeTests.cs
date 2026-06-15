using Stellar.Abstractions.Diagnostics;
using Xunit;

namespace Stellar.Application.Tests.Diagnostics;

// NOTE: PerfProbe is process-global static state. These tests mutate it via the
// internal test seam and reset it in a finally so they don't bleed into siblings.
// They are not run in parallel with other PerfProbe/PerfControls tests (xUnit
// runs tests in one class sequentially; cross-class isolation is via Reset()).
[Collection("PerfStaticState")]
public sealed class PerfProbeTests
{
    [Fact]
    public void Disabled_BeginEnd_AreNoOps_AndSnapshotIsZero()
    {
        PerfProbe.OverrideEnabledForTests(false);
        try
        {
            PerfProbe.ResetForTests();
            PerfProbe.BeginDraw();
            PerfProbe.EndDraw();
            PerfProbe.BeginWindow("w");
            PerfProbe.EndWindow("w");
            PerfProbe.RecordFrame(0.016);

            var snap = PerfProbe.Snapshot();
            Assert.Equal(0.0, snap.LastFrameMs);
            Assert.Empty(snap.WindowMs);
        }
        finally { PerfProbe.OverrideEnabledForTests(false); PerfProbe.ResetForTests(); }
    }

    [Fact]
    public void Enabled_RecordFrame_StoresFrameMs_AndIncrementsCounter()
    {
        PerfProbe.OverrideEnabledForTests(true);
        try
        {
            PerfProbe.ResetForTests();
            var startFrame = PerfProbe.FrameCounter;

            PerfProbe.RecordFrame(0.020);   // 20 ms => 50 fps
            PerfProbe.RecordFrame(0.010);   // 10 ms => 100 fps

            var snap = PerfProbe.Snapshot();
            Assert.Equal(10.0, snap.LastFrameMs, 3);
            Assert.Equal(startFrame + 2, PerfProbe.FrameCounter);
        }
        finally { PerfProbe.OverrideEnabledForTests(false); PerfProbe.ResetForTests(); }
    }

    [Fact]
    public void Enabled_PerWindowTime_AccumulatesAcrossPasses_WithinAFrame()
    {
        PerfProbe.OverrideEnabledForTests(true);
        try
        {
            PerfProbe.ResetForTests();

            // Two OnGUI passes (Layout + Repaint) within one Unity frame: the
            // same window is timed twice, then the frame is committed once.
            PerfProbe.BeginWindow("party"); Spin(); PerfProbe.EndWindow("party");
            PerfProbe.BeginWindow("party"); Spin(); PerfProbe.EndWindow("party");
            PerfProbe.RecordFrame(0.016);

            var snap = PerfProbe.Snapshot();
            Assert.True(snap.WindowMs.ContainsKey("party"));
            Assert.True(snap.WindowMs["party"] > 0.0);
        }
        finally { PerfProbe.OverrideEnabledForTests(false); PerfProbe.ResetForTests(); }
    }

    [Fact]
    public void RecordFrame_ResetsPerFrameWindowAccumulators()
    {
        PerfProbe.OverrideEnabledForTests(true);
        try
        {
            PerfProbe.ResetForTests();
            PerfProbe.BeginWindow("a"); Spin(); PerfProbe.EndWindow("a");
            PerfProbe.RecordFrame(0.016);   // commits frame 1: "a" present

            // Frame 2 touches only "b". After committing, the snapshot reflects
            // frame 2's per-window map — "a" did no work this frame so it is 0/absent.
            PerfProbe.BeginWindow("b"); Spin(); PerfProbe.EndWindow("b");
            PerfProbe.RecordFrame(0.016);

            var snap = PerfProbe.Snapshot();
            Assert.True(snap.WindowMs.ContainsKey("b"));
            Assert.False(snap.WindowMs.ContainsKey("a"));
        }
        finally { PerfProbe.OverrideEnabledForTests(false); PerfProbe.ResetForTests(); }
    }

    private static void Spin()
    {
        // Burn a little wall-clock so the Stopwatch delta is reliably > 0.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < 0.05) { }
    }
}
