using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>Background warm of a pre-warmed window (2026-11-09): the Settings hub stays active off-screen after its
/// mount until its time-sliced tabs are laid out and its bubbles settled, so its first open is a plain re-show. These
/// pin when the warm ends and when a revealed prebuild branch may be put away.</summary>
public sealed class BackgroundWarmRulesTests
{
    [Fact]
    public void NeverParksBeforeTheMountTicksEvenWhenDone() =>
        Assert.False(BackgroundWarmRules.ShouldPark(ticks: 2, mountTicks: 3, maxTicks: 100, done: true));

    [Fact]
    public void ParksOnceDone() =>
        Assert.True(BackgroundWarmRules.ShouldPark(ticks: 3, mountTicks: 3, maxTicks: 100, done: true));

    [Fact]
    public void KeepsWarmingWhileNotDone() =>
        Assert.False(BackgroundWarmRules.ShouldPark(ticks: 50, mountTicks: 3, maxTicks: 100, done: false));

    [Fact]
    public void ParksAtTheCapEvenIfNeverDone() =>
        Assert.True(BackgroundWarmRules.ShouldPark(ticks: 100, mountTicks: 3, maxTicks: 100, done: false));

    [Theory]
    [InlineData(false, 10, 0, false)]  // a tab still building / laying out
    [InlineData(true, 2, 0, false)]    // bubbles still owe their settle measures
    [InlineData(true, 3, 4, false)]    // background measures deferred by the budget
    [InlineData(true, 3, 0, true)]
    public void DoneNeedsIdlePrebuildSettleAppliesAndNoPendingMeasures(bool idle, int applies, int pending, bool expected) =>
        Assert.Equal(expected, BackgroundWarmRules.IsDone(idle, applies, pending));

    [Theory]
    [InlineData(0, 0, false)]    // no apply since the reveal ended: texts not filled yet
    [InlineData(1, 0, true)]
    [InlineData(1, 5, false)]    // budgeted bubble measures still pending
    [InlineData(60, 5, true)]    // waited the cap: put it away anyway
    public void RevealSettlesAfterAnApplyAndItsMeasures(int applies, int pending, bool expected) =>
        Assert.Equal(expected, BackgroundWarmRules.RevealSettled(applies, pending, maxWait: 60));

    // Launcher re-warm (11-09): warmed at the title screen, its tiles change after world entry while it is hidden.
    [Fact]
    public void TheFirstSampleIsOnlyABaseline() => Assert.False(new ContentWatch().Changed(5, shown: false));

    [Fact]
    public void AChangeWhileHiddenIsReportedOnce()
    {
        var w = new ContentWatch();
        w.Reset(1);
        Assert.False(w.Changed(1, shown: false));
        Assert.True(w.Changed(2, shown: false));
        Assert.False(w.Changed(2, shown: false));   // the baseline moved: one re-warm per change
    }

    [Fact]
    public void AChangeWhileShownIsNoReason()
    {
        var w = new ContentWatch();
        w.Reset(1);
        Assert.False(w.Changed(2, shown: true));    // its bindings pulled it in
        Assert.False(w.Changed(2, shown: false));   // ...so hiding it again needs no re-warm
        Assert.Equal(2, w.Baseline);
    }
}
