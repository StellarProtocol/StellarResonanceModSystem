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

    // First open after the 11-09 pre-warm fix: every tab is parked and re-warms; with its bindings running each bubble
    // re-measured on its inactive→active edge (~50 measures per tab in one apply).
    [Fact]
    public void AFilledBranchRewarmsWithItsBindingsOff() => Assert.False(KeepAliveRules.RunBindingsWhileRewarming(filled: true));

    [Fact]
    public void ACutShortFirstWarmStillRunsItsBindingsToFillItsTexts() => Assert.True(KeepAliveRules.RunBindingsWhileRewarming(filled: false));

    // Game UI tab, 11-09: filled at the title screen, its HUD rows resolve only in the world — re-pulled on show (42 ms).
    [Theory]
    [InlineData(true, false, false)]   // filled, data unchanged: pure re-activation
    [InlineData(true, true, true)]     // filled, data changed while hidden: pull it in the background
    [InlineData(false, false, true)]   // never filled: always
    public void ARewarmRunsBindingsWhenUnfilledOrItsContentChanged(bool filled, bool changed, bool expected) =>
        Assert.Equal(expected, KeepAliveRules.RunBindingsWhileRewarming(filled, changed));

    [Theory]
    [InlineData((int)KeepAliveState.Parked, false, true, true)]
    [InlineData((int)KeepAliveState.Parked, false, false, false)]   // nothing changed
    [InlineData((int)KeepAliveState.Parked, true, true, false)]     // already refreshing
    [InlineData((int)KeepAliveState.Shown, false, true, false)]     // its bindings run anyway
    [InlineData((int)KeepAliveState.Warming, false, true, false)]
    [InlineData((int)KeepAliveState.Cold, false, true, false)]      // built/filled on its first show
    public void OnlyAParkedBranchWithChangedContentStartsARefresh(int s, bool refreshing, bool changed, bool expected) =>
        Assert.Equal(expected, KeepAliveRules.ShouldStartRefresh((KeepAliveState)s, refreshing, changed));

    [Theory]
    [InlineData(2, 0, false)]    // bubbles still owe settle measures
    [InlineData(3, 4, false)]    // budgeted measures pending
    [InlineData(3, 0, true)]
    [InlineData(60, 4, true)]    // the cap
    public void ARefreshEndsAfterTheSettleAppliesAndItsMeasures(int applies, int pending, bool expected) =>
        Assert.Equal(expected, KeepAliveRules.RefreshSettled(applies, pending, maxWait: 60));

    [Fact]
    public void ARefreshingParkedBranchIsLiveInTheBackground()
    {
        var g = new AliveGate { Refreshing = true };
        Assert.True(g.Live);
        Assert.True(g.Background);   // budgeted measures, branch-only re-layout
        Assert.False(g.Visible);
    }

    [Fact]
    public void BackgroundMeansLiveButNotVisible()
    {
        var g = new AliveGate();
        Assert.False(g.Background);                      // parked: not live at all
        g.Warming = true; Assert.True(g.Background);     // warming off screen
        g.Warming = false; g.Shown = true; Assert.False(g.Background);
    }

    [Fact]
    public void AShownBranchInsideAWarmingOneIsStillBackground()
    {
        var outer = new AliveGate { Warming = true };
        var inner = new AliveGate { Parent = outer, Shown = true };
        Assert.True(inner.Background);   // live through the warming outer branch, but not on screen
        outer.Warming = false; outer.Shown = true;
        Assert.False(inner.Background);
    }

    [Fact]
    public void OnlyBindingsInAWarmingBranchAreInBackground()
    {
        Assert.False(GatedBinding.InBackground(null));
        Assert.False(GatedBinding.InBackground(new Probe()));   // outside any KeepAlive branch: always visible-path
        var b = new Probe { Alive = new AliveGate { Warming = true } };
        Assert.True(GatedBinding.InBackground(b));
        b.Alive!.Shown = true;
        Assert.False(GatedBinding.InBackground(b));
    }
}
