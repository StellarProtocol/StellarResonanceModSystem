using System.Collections.Generic;
using Stellar.Infrastructure.Unity;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Recon § Run 9 R9-3: at Time.timeScale = 0 the framework tick (InvokeRepeating, scaled) fired 0 times in 45/90/300 s
// pauses — every plugin Update, hotkey, toast and Photo Studio's own logic stopped, and chat/combat events only queued.
// While paused a per-frame driver ticks from REAL time at the configured rate; unpaused the scheduled driver behaves as
// before; never two ticks in one frame across the switch. Pinned through the pure TickPacer. Do not weaken.
public sealed class TickPacerTests
{
    private const float Frame = 1f / 60f;

    [Fact]
    public void unscaled_tick_fires_while_paused_at_the_configured_rate()
    {
        var p = new TickPacer { Unscaled = true };
        p.Start(0f);
        var dts = new List<float>();
        for (var f = 1; f <= 60; f++)                            // one real second at 60 fps, clock paused
            if (p.TryUnscaled(f * Frame, f, hz: 10, out var dt)) dts.Add(dt);
        Assert.InRange(dts.Count, 9, 10);                        // ~10 Hz, never per frame
        Assert.All(dts, dt => Assert.InRange(dt, 0.099f, 0.117f));   // real seconds since the previous tick
    }

    [Fact]
    public void unscaled_tick_never_fires_when_not_paused()
    {
        var p = new TickPacer();
        p.Start(0f);
        for (var f = 1; f <= 120; f++) Assert.False(p.TryUnscaled(f * Frame, f, 10, out _));
        Assert.False(new TickPacer { Unscaled = true }.TryUnscaled(5f, 1, hz: 0, out _));   // no rate: never
    }

    [Fact]
    public void scheduled_tick_is_unchanged_when_not_paused()
    {
        var p = new TickPacer();
        p.Start(1f);
        Assert.True(p.TryScheduled(1.1f, 10, out var dt1));
        Assert.Equal(0.1f, dt1, 4);                              // real seconds since Start, as before
        Assert.True(p.TryScheduled(1.15f, 11, out var dt2));     // every invoke on its own frame fires
        Assert.Equal(0.05f, dt2, 4);
        Assert.True(p.TryScheduled(1.4f, 30, out var dt3));
        Assert.Equal(0.25f, dt3, 4);
    }

    [Fact]
    public void no_frame_ticks_twice_across_the_pause_switch()
    {
        var p = new TickPacer { Unscaled = true };
        p.Start(0f);
        Assert.True(p.TryUnscaled(0.2f, 12, 10, out _));         // paused tick on frame 12 (the unfreeze runs inside it) …
        p.Unscaled = false;
        Assert.False(p.TryScheduled(0.2f, 12, out _));           // … the invoke on that same frame does not tick again
        Assert.True(p.TryScheduled(0.25f, 13, out var dt));      // the next frame does, dt from the paused tick
        Assert.Equal(0.05f, dt, 4);

        p.Unscaled = true;                                       // freeze pressed inside the scheduled tick on frame 20
        Assert.True(p.TryScheduled(0.5f, 20, out _));
        Assert.False(p.TryUnscaled(0.7f, 20, 10, out _));        // same frame: no second tick
        Assert.True(p.TryUnscaled(0.7f, 21, 10, out _));
    }
}
