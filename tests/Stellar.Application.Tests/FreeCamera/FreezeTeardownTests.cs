using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Freeze review round 2 (qa minor): the unfreeze ORDER was unpinned. RE-PINNED 2026-10-02 (late) for the global time pause:
// the per-entity restores (gate disarm, drawn speeds, factors, ECS layers, effects) are gone with the mechanisms they undid;
// the held animation requests are replayed and the hold is released while the clock is still stopped (the replayed states
// and the snap land on a paused frame), THEN the clock runs, and no failing step ever skips the rest — above all, a throwing hold release never leaves the game paused. Do not weaken.
public sealed class FreezeTeardownTests
{
    [Fact]
    public void freeze_unfreeze_order_replays_animation_and_releases_the_hold_before_the_clock_runs()
    {
        var steps = new Recorder();
        Assert.Null(FreezeTeardown.Run(steps));
        Assert.Equal(new[] { "ReleaseAnim", "StopHold", "ResumeClock", "ClearLedger" }, steps.Calls);
    }

    [Theory]
    [InlineData("ReleaseAnim")]
    [InlineData("StopHold")]
    [InlineData("ResumeClock")]
    [InlineData("ClearLedger")]
    public void freeze_unfreeze_order_a_failing_step_never_skips_the_rest(string failing)
    {
        var steps = new Recorder { Throw = failing };
        Assert.Equal(failing, Assert.IsType<InvalidOperationException>(FreezeTeardown.Run(steps)).Message);
        Assert.Equal(new[] { "ReleaseAnim", "StopHold", "ResumeClock", "ClearLedger" }, steps.Calls);
    }

    internal sealed class Recorder : IFreezeTeardownSteps
    {
        public readonly List<string> Calls = new();
        public string? Throw;

        public void ReleaseAnim() => Note(nameof(ReleaseAnim));
        public void StopHold() => Note(nameof(StopHold));
        public void ResumeClock() => Note(nameof(ResumeClock));
        public void ClearLedger() => Note(nameof(ClearLedger));

        private void Note(string step)
        {
            Calls.Add(step);
            if (step == Throw) throw new InvalidOperationException(step);
        }
    }
}
