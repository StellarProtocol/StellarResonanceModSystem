using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>ConditionalElement.KeepAlive (2026-11-09): after the background pre-build, a Settings tab switch still cost
/// 45-65 ms because the hidden tab was deactivated and re-enabled (re-mesh + whole-window re-layout + bubble re-measures).
/// A kept-alive tab is parked instead; these pin the state rules, the binding gate and the re-warm plan.</summary>
public sealed class KeepAliveRulesTests
{
    [Theory]
    [InlineData((int)KeepAliveState.Cold, (int)KeepAliveAction.Activate)]      // never kept alive yet: today's full activation
    [InlineData((int)KeepAliveState.Warming, (int)KeepAliveAction.FinishWarm)] // mid background layout: finish it now, then show
    [InlineData((int)KeepAliveState.Parked, (int)KeepAliveAction.Unpark)]      // the cheap path
    [InlineData((int)KeepAliveState.Shown, (int)KeepAliveAction.None)]
    public void ShowAlwaysEndsShown(int fromState, int expectedAction)
    {
        var (from, act) = ((KeepAliveState)fromState, (KeepAliveAction)expectedAction);
        var (next, a) = KeepAliveRules.OnShow(from);
        Assert.Equal(KeepAliveState.Shown, next);
        Assert.Equal(act, a);
    }

    [Fact]
    public void HidingTheShownBranchParksItInsteadOfDeactivating() =>
        Assert.Equal((KeepAliveState.Parked, KeepAliveAction.Park), KeepAliveRules.OnHide(KeepAliveState.Shown));

    [Fact]
    public void HidingAColdBranchDeactivatesItLikeAPlainConditional() =>
        Assert.Equal((KeepAliveState.Cold, KeepAliveAction.Deactivate), KeepAliveRules.OnHide(KeepAliveState.Cold));

    [Theory]
    [InlineData((int)KeepAliveState.Warming)]
    [InlineData((int)KeepAliveState.Parked)]
    public void HidingANonShownAliveBranchChangesNothing(int state) =>
        Assert.Equal(((KeepAliveState)state, KeepAliveAction.None), KeepAliveRules.OnHide((KeepAliveState)state));

    [Theory]
    [InlineData((int)KeepAliveState.Parked)]
    [InlineData((int)KeepAliveState.Warming)]
    public void WindowReshowRewarmsParkedBranchesThatHaveUnits(int state) =>
        Assert.Equal((KeepAliveState.Warming, KeepAliveAction.Rewarm), KeepAliveRules.OnWindowReshow((KeepAliveState)state, hasUnits: true));

    [Theory]
    [InlineData((int)KeepAliveState.Parked)]
    [InlineData((int)KeepAliveState.Warming)]
    public void WindowReshowSendsParkedBranchesWithoutUnitsCold(int state) =>
        Assert.Equal((KeepAliveState.Cold, KeepAliveAction.Deactivate), KeepAliveRules.OnWindowReshow((KeepAliveState)state, hasUnits: false));

    [Theory]
    [InlineData((int)KeepAliveState.Shown)]   // re-activates with the window, as any visible content
    [InlineData((int)KeepAliveState.Cold)]    // inactive: the root re-activation doesn't reach it
    public void WindowReshowLeavesShownAndColdBranches(int state) =>
        Assert.Equal(((KeepAliveState)state, KeepAliveAction.None), KeepAliveRules.OnWindowReshow((KeepAliveState)state, hasUnits: true));

    [Fact]
    public void GateIsLiveWhileShownOrWarmingOnly()
    {
        var g = new AliveGate();
        Assert.False(g.Live);
        g.Warming = true; Assert.True(g.Live);
        g.Warming = false; g.Shown = true; Assert.True(g.Live);
    }

    [Fact]
    public void NestedGateNeedsEveryEnclosingBranchLive()
    {
        var outer = new AliveGate { Shown = false };
        var inner = new AliveGate { Parent = outer, Shown = true };
        Assert.False(inner.Live);
        outer.Shown = true;
        Assert.True(inner.Live);
    }

    private sealed class Probe : GatedBinding { }

    [Fact]
    public void UngatedBindingsAlwaysRunGatedOnesFollowTheirGate()
    {
        Assert.True(GatedBinding.Runs(null));                // an interface-typed binding that isn't a GatedBinding
        Assert.True(GatedBinding.Runs(new Probe()));          // built outside any KeepAlive branch
        var gated = new Probe { Alive = new AliveGate() };
        Assert.False(GatedBinding.Runs(gated));               // parked
        gated.Alive!.Shown = true;
        Assert.True(GatedBinding.Runs(gated));
    }

    [Fact]
    public void RewarmPlanKeepsBuildOrderTakesActiveAndPendingButNeverBindingHiddenUnits()
    {
        // a,b active; c hidden by a binding (not ours); d still pending from an interrupted warm; e active.
        var units = new[] { "a", "b", "c", "d", "e" };
        var active = new HashSet<string> { "a", "b", "e" };
        var plan = KeepAliveRules.RewarmPlan(units, new HashSet<string> { "d" }, u => active.Contains(u));
        Assert.Equal(new[] { "a", "b", "d", "e" }, plan);
    }

    [Fact]
    public void RewarmPlanIsEmptyWhenNothingIsActiveOrPending() =>
        Assert.Empty(KeepAliveRules.RewarmPlan(new[] { "a", "b" }, new HashSet<string>(), _ => false));
}
