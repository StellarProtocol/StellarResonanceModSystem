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
            // MarkDrawFrame reflects that Band 3 (draw) ran this tick.
            PerfProbe.BeginWindow("party"); Spin(); PerfProbe.EndWindow("party");
            PerfProbe.BeginWindow("party"); Spin(); PerfProbe.EndWindow("party");
            PerfProbe.MarkDrawFrame();
            PerfProbe.RecordFrame(0.016);

            var snap = PerfProbe.Snapshot();
            Assert.True(snap.WindowMs.ContainsKey("party"));
            Assert.True(snap.WindowMs["party"] > 0.0);
        }
        finally { PerfProbe.OverrideEnabledForTests(false); PerfProbe.ResetForTests(); }
    }

    [Fact]
    public void RecordFrame_DrawFrame_PublishesWindowMs_AndDropsClosedWindows()
    {
        PerfProbe.OverrideEnabledForTests(true);
        try
        {
            PerfProbe.ResetForTests();

            // Draw frame 1: window "a" drawn. MarkDrawFrame ensures the guard is set.
            PerfProbe.BeginWindow("a"); Spin(); PerfProbe.EndWindow("a");
            PerfProbe.MarkDrawFrame();
            PerfProbe.RecordFrame(0.016);

            var snap1 = PerfProbe.Snapshot();
            Assert.True(snap1.WindowMs.ContainsKey("a"));

            // Draw frame 2: only "b" drawn. Published map is replaced — "a" should be gone
            // (the draw-frame republish does Clear() + repopulate, so closed windows drop).
            PerfProbe.BeginWindow("b"); Spin(); PerfProbe.EndWindow("b");
            PerfProbe.MarkDrawFrame();
            PerfProbe.RecordFrame(0.016);

            var snap2 = PerfProbe.Snapshot();
            Assert.True(snap2.WindowMs.ContainsKey("b"));
            Assert.False(snap2.WindowMs.ContainsKey("a"), "closed window 'a' must be removed on next draw frame");
        }
        finally { PerfProbe.OverrideEnabledForTests(false); PerfProbe.ResetForTests(); }
    }

    [Fact]
    public void RecordFrame_NonDrawTick_HoldsLastPublishedWindowMs()
    {
        PerfProbe.OverrideEnabledForTests(true);
        try
        {
            PerfProbe.ResetForTests();

            // Draw frame: publish window "a".
            PerfProbe.BeginWindow("a"); Spin(); PerfProbe.EndWindow("a");
            PerfProbe.MarkDrawFrame();
            PerfProbe.RecordFrame(0.016);
            var drawSnap = PerfProbe.Snapshot();
            var drawMs = drawSnap.WindowMs["a"];
            Assert.True(drawMs > 0.0);

            // Fast master tick (no MarkDrawFrame, no window timing): published map must be HELD.
            PerfProbe.RecordFrame(0.004);

            var holdSnap = PerfProbe.Snapshot();
            Assert.True(holdSnap.WindowMs.ContainsKey("a"), "published window ms must be held on non-draw ticks");
            Assert.Equal(drawMs, holdSnap.WindowMs["a"], 6);
        }
        finally { PerfProbe.OverrideEnabledForTests(false); PerfProbe.ResetForTests(); }
    }

    [Fact]
    public void RecordFrame_NonDrawTick_StillUpdatesFpsAndUpdateCpu()
    {
        PerfProbe.OverrideEnabledForTests(true);
        try
        {
            PerfProbe.ResetForTests();

            // Draw frame to establish a baseline.
            PerfProbe.MarkDrawFrame();
            PerfProbe.RecordFrame(0.033);   // ~30 fps

            var startCounter = PerfProbe.FrameCounter;

            // Fast master tick — no MarkDrawFrame. FrameCounter and LastFrameMs (FPS) must still update.
            PerfProbe.BeginUpdate(); Spin(); PerfProbe.EndUpdate();
            PerfProbe.RecordFrame(0.004);   // 250 Hz master rate

            var snap = PerfProbe.Snapshot();
            Assert.Equal(startCounter + 1, PerfProbe.FrameCounter);
            Assert.Equal(4.0, snap.LastFrameMs, 3);
            Assert.True(snap.LastUpdateMs > 0.0);
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
