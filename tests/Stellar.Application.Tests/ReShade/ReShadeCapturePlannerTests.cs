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
        var planner = new ReShadeCapturePlanner(shaped: true, new[] { WithDepth("DepthOfField") }, nowMs: 0);

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
}
