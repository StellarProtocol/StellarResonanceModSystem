using System;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class ReShadeCapturePlannerTests
{
    private static ReShadeTechnique NoDepth(string name) => new(name, name + ".fx", true, false);
    private static ReShadeTechnique WithDepth(string name) => new(name, name + ".fx", true, true);

    [Fact]
    public void Unshaped_warms_up_then_renders_once_then_done_true()
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);

        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, nowMs: 10));
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, nowMs: 20));
        Assert.IsType<CaptureStep.Render>(planner.Next(lastDrawn: 1, nowMs: 30));

        var done = Assert.IsType<CaptureStep.Done>(planner.Next(lastDrawn: 1, nowMs: 40));
        Assert.True(done.Applied);
    }

    [Fact]
    public void Shaped_with_depth_techniques_disables_then_warms_renders_restores_then_done_true()
    {
        var active = new[] { NoDepth("Bloom"), WithDepth("DepthOfField") };
        var planner = new ReShadeCapturePlanner(shaped: true, active, nowMs: 0);

        var disable = Assert.IsType<CaptureStep.DisableDepth>(planner.Next(lastDrawn: 0, nowMs: 0));
        Assert.Equal(new[] { new ReShadeTechniqueRef("DepthOfField.fx", "DepthOfField") }, disable.Techniques);

        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, nowMs: 10));
        Assert.IsType<CaptureStep.Render>(planner.Next(lastDrawn: 1, nowMs: 20));

        var restore = Assert.IsType<CaptureStep.Restore>(planner.Next(lastDrawn: 1, nowMs: 30));
        Assert.Equal(new[] { new ReShadeTechniqueRef("DepthOfField.fx", "DepthOfField") }, restore.Techniques);

        var done = Assert.IsType<CaptureStep.Done>(planner.Next(lastDrawn: 1, nowMs: 40));
        Assert.True(done.Applied);
    }

    [Fact]
    public void Shaped_without_depth_techniques_behaves_like_unshaped()
    {
        var planner = new ReShadeCapturePlanner(shaped: true, new[] { NoDepth("Bloom") }, nowMs: 0);

        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, nowMs: 10));
        Assert.IsType<CaptureStep.Render>(planner.Next(lastDrawn: 1, nowMs: 20));

        var done = Assert.IsType<CaptureStep.Done>(planner.Next(lastDrawn: 1, nowMs: 30));
        Assert.True(done.Applied);
    }

    [Fact]
    public void WarmUp_timeout_without_depth_disabled_goes_straight_to_done_false()
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);

        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, nowMs: 1000));

        var done = Assert.IsType<CaptureStep.Done>(
            planner.Next(lastDrawn: 0, nowMs: ReShadeCapturePlanner.WarmUpTimeoutMs));
        Assert.False(done.Applied);

        // Done is absorbing.
        var again = Assert.IsType<CaptureStep.Done>(planner.Next(lastDrawn: 0, nowMs: 999_999));
        Assert.False(again.Applied);
    }

    [Fact]
    public void WarmUp_timeout_with_depth_disabled_restores_then_done_false()
    {
        // Bloom stays drawable: a plan whose every technique is switched off ends at once (NothingActive) instead.
        var planner = new ReShadeCapturePlanner(shaped: true, new[] { NoDepth("Bloom"), WithDepth("DepthOfField") }, nowMs: 0);

        planner.Next(lastDrawn: 0, nowMs: 0); // DisableDepth — warm-up clock starts here.
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, nowMs: 1000));

        var restore = Assert.IsType<CaptureStep.Restore>(
            planner.Next(lastDrawn: 0, nowMs: ReShadeCapturePlanner.WarmUpTimeoutMs));
        Assert.Equal(new[] { new ReShadeTechniqueRef("DepthOfField.fx", "DepthOfField") }, restore.Techniques);

        var done = Assert.IsType<CaptureStep.Done>(planner.Next(lastDrawn: 0, nowMs: 999_999));
        Assert.False(done.Applied);
    }

    [Fact]
    public void No_active_techniques_is_done_false_on_the_first_call()
    {
        var planner = new ReShadeCapturePlanner(shaped: true, Array.Empty<ReShadeTechnique>(), nowMs: 0);

        var done = Assert.IsType<CaptureStep.Done>(planner.Next(lastDrawn: 0, nowMs: 0));
        Assert.False(done.Applied);
    }

    [Fact]
    public void Render_is_returned_exactly_once_across_many_calls()
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);

        var renderCount = 0;
        for (var frame = 0; frame < 50; frame++)
        {
            var nowMs = frame * 16L;
            var lastDrawn = frame < 3 ? 0 : 1;
            if (planner.Next(lastDrawn, nowMs) is CaptureStep.Render)
                renderCount++;
        }

        Assert.Equal(1, renderCount);
    }

    [Fact]
    public void WarmUpTimeoutMs_is_5000()
    {
        Assert.Equal(5000, ReShadeCapturePlanner.WarmUpTimeoutMs);
    }

    // Technique identity is (effect file, name): a same-named technique in another effect must never be touched.
    [Fact]
    public void Depth_steps_carry_the_effect_file_with_each_name()
    {
        var active = new[]
        {
            new ReShadeTechnique("Blur", "PackA/Blur.fx", true, false),
            new ReShadeTechnique("Blur", "DepthBlur.fx", true, true),
        };
        var planner = new ReShadeCapturePlanner(shaped: true, active, nowMs: 0);

        var disable = Assert.IsType<CaptureStep.DisableDepth>(planner.Next(lastDrawn: 0, nowMs: 0));
        Assert.Equal(new[] { new ReShadeTechniqueRef("DepthBlur.fx", "Blur") }, disable.Techniques);
        planner.Next(lastDrawn: 0, nowMs: 1);
        planner.Next(lastDrawn: 1, nowMs: 2);
        var restore = Assert.IsType<CaptureStep.Restore>(planner.Next(lastDrawn: 1, nowMs: 3));
        Assert.Equal(disable.Techniques, restore.Techniques);
    }

    // Fail fast: -1 (no ReShade runtime) and -3 (view creation failed) cannot recover by waiting out the 5 s.
    [Theory]
    [InlineData(-1)]
    [InlineData(-3)]
    public void A_hard_error_during_warm_up_restores_then_done_false_without_waiting(int code)
    {
        var planner = new ReShadeCapturePlanner(shaped: true, new[] { NoDepth("Bloom"), WithDepth("DepthOfField") }, nowMs: 0);
        planner.Next(lastDrawn: 0, nowMs: 0);   // DisableDepth
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, nowMs: 16));

        Assert.IsType<CaptureStep.Restore>(planner.Next(lastDrawn: code, nowMs: 32));
        Assert.False(Assert.IsType<CaptureStep.Done>(planner.Next(lastDrawn: code, nowMs: 48)).Applied);
        Assert.Equal(ReShadeWarmUpOutcome.Error, planner.Outcome);
        Assert.Equal(code, planner.WarmUpEndCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-3)]
    public void A_hard_error_without_depth_is_done_false_at_once(int code)
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, nowMs: 16));
        Assert.False(Assert.IsType<CaptureStep.Done>(planner.Next(lastDrawn: code, nowMs: 32)).Applied);
        Assert.Equal(ReShadeWarmUpOutcome.Error, planner.Outcome);
    }

    [Fact]
    public void Nothing_queued_keeps_warming_up()
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, nowMs: 16));
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: -2, nowMs: 32));
        Assert.Equal(ReShadeWarmUpOutcome.Pending, planner.Outcome);
    }

    [Fact]
    public void Outcome_records_drew_and_timeout()
    {
        var drew = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);
        drew.Next(0, 16);
        drew.Next(2, 32);
        Assert.Equal((ReShadeWarmUpOutcome.Drew, 2), (drew.Outcome, drew.WarmUpEndCode));

        var slow = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);
        slow.Next(0, 16);
        slow.Next(0, ReShadeCapturePlanner.WarmUpTimeoutMs);
        Assert.Equal(ReShadeWarmUpOutcome.TimedOut, slow.Outcome);
    }

    // ---- Fix 2 (2026-10-04): "ReShade was not ready" with warmUpEndCode=8 renderCode=0. After the first warm-up compiled
    // the effects at the new size, ReShade's preset re-apply switched on another technique; the next warm-up queued its
    // compile and the real render ran while ReShade was loading (render_effects returns early -> 0 drawn).

    [Fact]
    public void Loading_waits_instead_of_rendering_even_after_a_warm_up_drew()
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, loading: false, nowMs: 16));
        Assert.IsType<CaptureStep.Wait>(planner.Next(lastDrawn: 8, loading: true, nowMs: 32));
        Assert.IsType<CaptureStep.Wait>(planner.Next(lastDrawn: 8, loading: true, nowMs: 48));
        // Loading is over, but the 8 drawn predates it: warm up again before the real render.
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 8, loading: false, nowMs: 64));
        Assert.IsType<CaptureStep.Render>(planner.Next(lastDrawn: 9, loading: false, nowMs: 80));
    }

    [Fact]
    public void A_render_that_drew_nothing_goes_back_to_warm_up_instead_of_a_photo_without_ReShade()
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);
        planner.Next(lastDrawn: 0, loading: false, nowMs: 16);
        Assert.IsType<CaptureStep.Render>(planner.Next(lastDrawn: 8, loading: false, nowMs: 32));

        Assert.Equal(RenderVerdict.Retry, planner.AfterRender(renderCode: 0, nowMs: 600));
        Assert.Equal(ReShadeWarmUpOutcome.Pending, planner.Outcome);

        Assert.IsType<CaptureStep.Wait>(planner.Next(lastDrawn: 0, loading: true, nowMs: 616));
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(lastDrawn: 0, loading: false, nowMs: 632));
        Assert.IsType<CaptureStep.Render>(planner.Next(lastDrawn: 9, loading: false, nowMs: 648));
        Assert.Equal(RenderVerdict.Keep, planner.AfterRender(renderCode: 9, nowMs: 700));
        Assert.Equal(ReShadeWarmUpOutcome.Drew, planner.Outcome);
        Assert.True(Assert.IsType<CaptureStep.Done>(planner.Next(lastDrawn: 9, loading: false, nowMs: 716)).Applied);
    }

    [Fact]
    public void A_render_that_drew_nothing_past_the_deadline_keeps_the_photo_without_ReShade()
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);
        planner.Next(lastDrawn: 0, loading: false, nowMs: 16);
        planner.Next(lastDrawn: 8, loading: false, nowMs: 32);

        Assert.Equal(RenderVerdict.KeepWithoutReShade, planner.AfterRender(0, ReShadeCapturePlanner.WarmUpTimeoutMs));
        Assert.Equal(ReShadeWarmUpOutcome.RenderDrewNothing, planner.Outcome);
        Assert.False(Assert.IsType<CaptureStep.Done>(planner.Next(0, false, ReShadeCapturePlanner.WarmUpTimeoutMs + 16)).Applied);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-3)]
    public void A_render_hard_error_keeps_the_photo_without_ReShade_at_once(int code)
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);
        planner.Next(lastDrawn: 0, loading: false, nowMs: 16);
        planner.Next(lastDrawn: 8, loading: false, nowMs: 32);

        Assert.Equal(RenderVerdict.KeepWithoutReShade, planner.AfterRender(code, nowMs: 48));
        Assert.Equal(ReShadeWarmUpOutcome.Error, planner.Outcome);
    }

    [Fact]
    public void A_drew_nothing_retry_with_depth_disabled_keeps_the_overrides_until_the_end()
    {
        var planner = new ReShadeCapturePlanner(shaped: true, new[] { NoDepth("Bloom"), WithDepth("MXAO") }, nowMs: 0);
        Assert.IsType<CaptureStep.DisableDepth>(planner.Next(0, false, 0));
        planner.Next(0, false, 16);
        Assert.IsType<CaptureStep.Render>(planner.Next(4, false, 32));
        Assert.Equal(RenderVerdict.Retry, planner.AfterRender(0, 48));
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(0, false, 64));   // no Restore in between
        Assert.IsType<CaptureStep.Render>(planner.Next(4, false, 80));
        Assert.Equal(RenderVerdict.Keep, planner.AfterRender(4, 96));
        Assert.IsType<CaptureStep.Restore>(planner.Next(4, false, 112));
        Assert.True(Assert.IsType<CaptureStep.Done>(planner.Next(4, false, 128)).Applied);
    }

    [Fact]
    public void Loading_time_does_not_count_against_the_warm_up_deadline()
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);
        planner.Next(0, false, 0);
        for (var t = 16L; t <= 10_000; t += 16)
            Assert.IsType<CaptureStep.Wait>(planner.Next(0, true, t));
        // 10 s of loading, then 2 s of warm-up: still inside the 5 s of non-loading time.
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(0, false, 12_000));
        Assert.Equal(ReShadeWarmUpOutcome.Pending, planner.Outcome);
    }

    [Fact]
    public void Loading_never_waits_past_the_hard_cap()
    {
        var planner = new ReShadeCapturePlanner(shaped: false, new[] { NoDepth("Bloom") }, nowMs: 0);
        planner.Next(0, false, 0);
        CaptureStep step = new CaptureStep.Wait();
        for (var t = 16L; t <= ReShadeCapturePlanner.MaxWaitMs && step is not CaptureStep.Done; t += 16)
            step = planner.Next(0, true, t);
        Assert.False(Assert.IsType<CaptureStep.Done>(step).Applied);
        Assert.Equal(ReShadeWarmUpOutcome.TimedOut, planner.Outcome);
        Assert.Equal(20_000, ReShadeCapturePlanner.MaxWaitMs);
    }

    // ---- Fix 1: size-locked effects. A shaped photo cannot fall back to screen size, so they are left out of it.

    [Fact]
    public void Skipping_size_locked_disables_them_with_the_depth_ones()
    {
        var locked = NoDepth("Draft") with { SizeLocked = true };
        var planner = new ReShadeCapturePlanner(shaped: true, new[] { NoDepth("Bloom"), WithDepth("MXAO"), locked }, nowMs: 0,
            skipSizeLocked: true);
        var disable = Assert.IsType<CaptureStep.DisableDepth>(planner.Next(0, false, 0));
        Assert.Equal(new[] { new ReShadeTechniqueRef("MXAO.fx", "MXAO"), new ReShadeTechniqueRef("Draft.fx", "Draft") },
            disable.Techniques);
    }

    [Fact]
    public void Size_locked_techniques_are_kept_when_not_asked_to_skip_them()
    {
        var locked = NoDepth("Draft") with { SizeLocked = true };
        var planner = new ReShadeCapturePlanner(shaped: true, new[] { locked }, nowMs: 0);
        Assert.IsType<CaptureStep.WarmUp>(planner.Next(0, false, 0));
    }

    // Before: a shaped photo whose active techniques all used depth disabled them all and waited out the 5 s warm-up.
    [Fact]
    public void Nothing_left_after_skipping_is_done_false_at_once_instead_of_a_5_s_timeout()
    {
        var locked = NoDepth("Draft") with { SizeLocked = true };
        var planner = new ReShadeCapturePlanner(shaped: true, new[] { WithDepth("MXAO"), locked }, nowMs: 0, skipSizeLocked: true);
        Assert.False(Assert.IsType<CaptureStep.Done>(planner.Next(0, false, 0)).Applied);
        Assert.Equal(ReShadeWarmUpOutcome.NothingActive, planner.Outcome);
    }

    [Theory]
    [InlineData((int)ReShadeWarmUpOutcome.TimedOut, ReShadeCaptureNotes.NotReady)]
    [InlineData((int)ReShadeWarmUpOutcome.Error, ReShadeCaptureNotes.Error)]
    [InlineData((int)ReShadeWarmUpOutcome.RenderDrewNothing, ReShadeCaptureNotes.DrewNothing)]
    [InlineData((int)ReShadeWarmUpOutcome.NothingActive, ReShadeCaptureNotes.NothingToDraw)]
    public void Each_failure_has_its_own_note(int outcome, string note)
    {
        Assert.Equal(note, ReShadeCaptureNotes.For((ReShadeWarmUpOutcome)outcome));
    }

    [Fact]
    public void The_not_ready_note_text_is_unchanged_for_plugins_that_match_it()
    {
        Assert.Equal("ReShade was not ready — photo taken without it.", ReShadeCaptureNotes.NotReady);
    }
}
