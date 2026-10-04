using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

// The isolated capture (bridge 1.1.0): the photo is drawn in a separate ReShade runtime whose back buffer has the photo's
// size, so size-locked effects work at 2x/4x. The plan must always end a session it began, and must fall back (never
// hang, never ship a garbage photo) on anything unexpected.
public sealed class IsolatedCapturePlannerTests
{
    private static readonly ReShadeTechnique Locked = new("Draft", "Draft.fx", true, false) { SizeLocked = true };
    private static readonly ReShadeTechnique Depth = new("MXAO", "MXAO.fx", true, true);

    private static IsolatedCapturePlanner Planner(bool supported = true, bool shaped = false, params ReShadeTechnique[] active) =>
        new(active.Length == 0 ? new[] { Locked } : active, shaped, supported, nowMs: 0);

    private static IsolatedCapturePlanner Begun(long nowMs = 0)
    {
        var planner = Planner();
        Assert.IsType<IsolatedStep.Begin>(planner.Next(state: 0, nowMs));
        planner.AfterBegin(1, nowMs);
        return planner;
    }

    [Fact]
    public void A_ready_runtime_renders_once_then_ends_successfully()
    {
        var planner = Begun();
        Assert.IsType<IsolatedStep.Pump>(planner.Next(state: 1, nowMs: 16));
        Assert.IsType<IsolatedStep.Pump>(planner.Next(state: 2, nowMs: 32));
        Assert.True(Assert.IsType<IsolatedStep.Render>(planner.Next(state: 3, nowMs: 48)).FreshCamera);
        planner.AfterRender(5, nowMs: 64);
        Assert.True(Assert.IsType<IsolatedStep.End>(planner.Next(state: 3, nowMs: 80)).Success);
        Assert.False(planner.NeedsEnd);
        Assert.True(Assert.IsType<IsolatedStep.Done>(planner.Next(state: 5, nowMs: 96)).Success);
        Assert.Equal(IsolatedOutcome.Drew, planner.Outcome);
        Assert.Equal(3, planner.FinalState);
        Assert.Equal(5, planner.RenderCode);
        Assert.Equal(2, planner.FramesWaited);
    }

    [Fact]
    public void A_bridge_without_the_isolated_functions_falls_back_without_beginning()
    {
        var planner = Planner(supported: false);
        Assert.False(Assert.IsType<IsolatedStep.Done>(planner.Next(0, 0)).Success);
        Assert.Equal(IsolatedOutcome.Unsupported, planner.Outcome);
        Assert.False(planner.NeedsEnd);
    }

    [Fact]
    public void Depth_techniques_are_requested_off_for_every_isolated_photo()
    {
        var screen = Planner(supported: true, shaped: false, Locked, Depth);
        var begin = Assert.IsType<IsolatedStep.Begin>(screen.Next(0, 0));
        Assert.Equal(new[] { new ReShadeTechniqueRef("MXAO.fx", "MXAO") }, begin.DepthOff);
        Assert.Equal(ReShadeCaptureNotes.DepthLeftOut, screen.SuccessNote);   // a screen-shaped photo says so

        var shaped = Planner(supported: true, shaped: true, Locked, Depth);
        Assert.Equal(begin.DepthOff, Assert.IsType<IsolatedStep.Begin>(shaped.Next(0, 0)).DepthOff);
        Assert.Null(shaped.SuccessNote);   // a shaped photo always leaves depth out (documented), as before
    }

    [Fact]
    public void No_note_when_no_depth_technique_was_left_out()
    {
        Assert.Null(Planner().SuccessNote);
    }

    [Fact]
    public void Nothing_but_depth_techniques_falls_back_without_beginning()
    {
        var planner = Planner(supported: true, shaped: false, Depth with { SizeLocked = true });
        Assert.False(Assert.IsType<IsolatedStep.Done>(planner.Next(0, 0)).Success);
        Assert.Equal(IsolatedOutcome.NothingToDraw, planner.Outcome);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-4)]
    [InlineData(-5)]
    [InlineData(-6)]
    [InlineData(-7)]
    [InlineData(-8)]
    [InlineData(-9)]
    public void An_error_state_ends_then_falls_back(int state)
    {
        var planner = Begun();
        Assert.IsType<IsolatedStep.Pump>(planner.Next(1, 16));
        Assert.False(Assert.IsType<IsolatedStep.End>(planner.Next(state, 32)).Success);
        Assert.False(Assert.IsType<IsolatedStep.Done>(planner.Next(0, 48)).Success);
        Assert.Equal(IsolatedOutcome.Error, planner.Outcome);
        Assert.Equal(state, planner.FinalState);
    }

    [Fact]
    public void Nothing_to_draw_ends_then_falls_back()
    {
        var planner = Begun();
        Assert.False(Assert.IsType<IsolatedStep.End>(planner.Next(4, 16)).Success);
        Assert.Equal(IsolatedOutcome.NothingToDraw, planner.Outcome);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-10)]
    [InlineData(-9)]
    public void A_refused_begin_still_ends_then_falls_back(int code)
    {
        var planner = Planner();
        planner.Next(0, 0);
        planner.AfterBegin(code, 0);
        Assert.True(planner.NeedsEnd);
        Assert.False(Assert.IsType<IsolatedStep.End>(planner.Next(0, 16)).Success);
        Assert.Equal(IsolatedOutcome.Error, planner.Outcome);
        Assert.Equal(code, planner.FinalState);
    }

    [Fact]
    public void Compiling_past_the_timeout_ends_then_falls_back()
    {
        var planner = Begun();
        IsolatedStep step = new IsolatedStep.Pump();
        var t = 0L;
        while (step is IsolatedStep.Pump && t <= IsolatedCapturePlanner.TimeoutMs)
            step = planner.Next(2, t += 16);
        Assert.False(Assert.IsType<IsolatedStep.End>(step).Success);
        Assert.Equal(IsolatedOutcome.TimedOut, planner.Outcome);
        Assert.Equal(20_000, IsolatedCapturePlanner.TimeoutMs);
    }

    [Theory]
    [InlineData(-4)]   // the runtime started loading again
    [InlineData(0)]    // drew nothing
    public void A_render_that_drew_nothing_pumps_and_renders_again(int code)
    {
        var planner = Begun();
        Assert.IsType<IsolatedStep.Render>(planner.Next(3, 16));
        planner.AfterRender(code, 32);
        Assert.IsType<IsolatedStep.Pump>(planner.Next(2, 48));
        Assert.IsType<IsolatedStep.Render>(planner.Next(3, 64));
        planner.AfterRender(2, 80);
        Assert.True(Assert.IsType<IsolatedStep.End>(planner.Next(3, 96)).Success);
    }

    [Fact]
    public void A_render_that_keeps_drawing_nothing_until_the_timeout_falls_back()
    {
        var planner = Begun();
        planner.Next(3, 16);
        planner.AfterRender(-4, IsolatedCapturePlanner.TimeoutMs);
        Assert.False(Assert.IsType<IsolatedStep.End>(planner.Next(3, IsolatedCapturePlanner.TimeoutMs + 16)).Success);
        Assert.Equal(IsolatedOutcome.TimedOut, planner.Outcome);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-5)]
    [InlineData(-9)]
    public void A_render_error_ends_then_falls_back(int code)
    {
        var planner = Begun();
        planner.Next(3, 16);
        planner.AfterRender(code, 32);
        Assert.False(Assert.IsType<IsolatedStep.End>(planner.Next(3, 48)).Success);
        Assert.Equal(IsolatedOutcome.Error, planner.Outcome);
        Assert.Equal(code, planner.RenderCode);
    }

    [Fact]
    public void NeedsEnd_covers_the_whole_session_so_the_grabber_can_end_it_on_a_throw()
    {
        var planner = Planner();
        Assert.False(planner.NeedsEnd);
        planner.Next(0, 0);   // Begin returned: the grabber may already have called begin()
        Assert.True(planner.NeedsEnd);
        planner.AfterBegin(1, 0);
        planner.Next(1, 16);
        Assert.True(planner.NeedsEnd);
        planner.MarkEnded();   // the grabber's finally ended it
        Assert.False(planner.NeedsEnd);
    }

    // Every scripted session that began ends exactly once, before Done, whatever the bridge reports.
    [Theory]
    [InlineData(new[] { 1, 2, 3 }, 5)]
    [InlineData(new[] { 1, -8 }, 0)]
    [InlineData(new[] { 4 }, 0)]
    [InlineData(new[] { 0, 5, 1, 3 }, -5)]
    [InlineData(new[] { 3 }, -4)]
    public void A_begun_session_always_ends_exactly_once(int[] states, int renderCode)
    {
        var planner = Begun();
        var steps = new List<IsolatedStep>();
        var t = 0L;
        for (var frame = 0; frame < 2000; frame++)
        {
            var state = frame < states.Length ? states[frame] : states[^1];
            var step = planner.Next(state, t += 16);
            steps.Add(step);
            if (step is IsolatedStep.Render) planner.AfterRender(renderCode, t);
            if (step is IsolatedStep.Done) break;
        }
        Assert.IsType<IsolatedStep.Done>(steps[^1]);
        Assert.Single(steps, s => s is IsolatedStep.End);
        Assert.IsType<IsolatedStep.End>(steps[^2]);
        Assert.False(planner.NeedsEnd);
    }

    // ---- Warm-up (2026-10-05, in-game: Stella Medium at 4x came out almost white). A fresh isolated runtime renders
    // once, so adaptive effects (prod80 Bloom's previous-frame average luma, AutoExposure, ...) start from 0. When an
    // active effect is temporal, the plan renders it every frame for a while — each time from the pristine camera copy —
    // before the real render.

    private static IsolatedCapturePlanner WarmBegun()
    {
        var planner = new IsolatedCapturePlanner(new[] { Locked }, shaped: false, supported: true, nowMs: 0, warmUp: true);
        planner.Next(0, 0);
        planner.AfterBegin(1, 0);
        return planner;
    }

    [Fact]
    public void Warm_up_renders_come_before_the_real_render_and_only_the_first_takes_the_camera()
    {
        var planner = WarmBegun();
        var t = 0L;
        var first = Assert.IsType<IsolatedStep.WarmUpRender>(planner.Next(3, t += 16));
        Assert.True(first.FreshCamera);
        Assert.True(planner.HoldsCameraCopy);
        var warmUps = 1;
        IsolatedStep step;
        while ((step = planner.Next(3, t += 16)) is IsolatedStep.WarmUpRender w)
        {
            Assert.False(w.FreshCamera);   // every later render starts from the pristine copy
            warmUps++;
        }
        var render = Assert.IsType<IsolatedStep.Render>(step);
        Assert.False(render.FreshCamera);
        Assert.True(warmUps >= IsolatedCapturePlanner.MinWarmUpFrames);
        Assert.True(t >= IsolatedCapturePlanner.WarmUpMs);
        Assert.Equal(warmUps, planner.WarmUps);
        planner.AfterRender(4, t);
        Assert.True(Assert.IsType<IsolatedStep.End>(planner.Next(3, t + 16)).Success);
        Assert.False(planner.HoldsCameraCopy);
    }

    [Fact]
    public void Warm_up_needs_both_the_time_and_the_frame_count()
    {
        var fast = WarmBegun();   // 1 ms frames: the time decides
        var t = 0L;
        var n = 0;
        while (fast.Next(3, ++t) is IsolatedStep.WarmUpRender) n++;
        Assert.True(t >= IsolatedCapturePlanner.WarmUpMs);

        var slow = WarmBegun();   // 500 ms frames: the frame count decides
        t = 0;
        n = 0;
        while (slow.Next(3, t += 500) is IsolatedStep.WarmUpRender) n++;
        Assert.Equal(IsolatedCapturePlanner.MinWarmUpFrames, n);
    }

    [Fact]
    public void Without_a_temporal_effect_there_is_no_warm_up_and_the_render_takes_the_camera()
    {
        var planner = Begun();
        var render = Assert.IsType<IsolatedStep.Render>(planner.Next(3, 16));
        Assert.True(render.FreshCamera);
        Assert.Equal(0, planner.WarmUps);
    }

    [Fact]
    public void A_reload_during_warm_up_pumps_then_continues_from_the_copy()
    {
        var planner = WarmBegun();
        Assert.IsType<IsolatedStep.WarmUpRender>(planner.Next(3, 16));
        Assert.IsType<IsolatedStep.Pump>(planner.Next(2, 32));
        Assert.False(Assert.IsType<IsolatedStep.WarmUpRender>(planner.Next(3, 48)).FreshCamera);
    }

    [Fact]
    public void A_retried_final_render_starts_from_the_copy()
    {
        var planner = Begun();
        Assert.True(Assert.IsType<IsolatedStep.Render>(planner.Next(3, 16)).FreshCamera);
        planner.AfterRender(-4, 32);
        Assert.IsType<IsolatedStep.Pump>(planner.Next(2, 48));
        Assert.False(Assert.IsType<IsolatedStep.Render>(planner.Next(3, 64)).FreshCamera);
    }

    [Theory]
    [InlineData(-8)]
    [InlineData(4)]
    public void An_error_during_warm_up_ends_and_releases(int state)
    {
        var planner = WarmBegun();
        planner.Next(3, 16);
        Assert.True(planner.HoldsCameraCopy);
        Assert.False(Assert.IsType<IsolatedStep.End>(planner.Next(state, 32)).Success);
        Assert.False(planner.HoldsCameraCopy);
        Assert.False(planner.NeedsEnd);
    }

    [Fact]
    public void Warm_up_counts_inside_the_session_bound()
    {
        var planner = WarmBegun();
        var t = IsolatedCapturePlanner.TimeoutMs - 100;   // ready only after a long first compile
        IsolatedStep step;
        while ((step = planner.Next(3, t += 16)) is IsolatedStep.WarmUpRender) { }
        Assert.False(Assert.IsType<IsolatedStep.End>(step).Success);
        Assert.Equal(IsolatedOutcome.TimedOut, planner.Outcome);
    }

    [Fact]
    public void A_throw_mid_warm_up_leaves_the_grabber_an_end_and_a_release_to_do()
    {
        var planner = WarmBegun();
        planner.Next(3, 16);
        Assert.True(planner.NeedsEnd);
        Assert.True(planner.HoldsCameraCopy);
        planner.MarkEnded();
        Assert.False(planner.NeedsEnd);
        Assert.False(planner.HoldsCameraCopy);
    }

    [Fact]
    public void Warm_up_values()
    {
        Assert.Equal(2000, IsolatedCapturePlanner.WarmUpMs);
        Assert.Equal(30, IsolatedCapturePlanner.MinWarmUpFrames);
    }
}
