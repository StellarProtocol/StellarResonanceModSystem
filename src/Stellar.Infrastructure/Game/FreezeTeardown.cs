using System;
namespace Stellar.Infrastructure.Game;

/// <summary>The unfreeze steps, in the order <see cref="FreezeTeardown.Run"/> calls them (implemented by
/// <see cref="GameFreezeBackend"/>; faked in unit tests).</summary>
internal interface IFreezeTeardownSteps
{
    /// <summary>Replays the animation requests held while paused (each model resumes in the state the game last asked for).</summary>
    void ReleaseAnim();

    /// <summary>Releases the position hold (held models snapped to their logical pose).</summary>
    void StopHold();

    /// <summary>Runs the game's clock again (<see cref="GameClockPause.Resume"/>).</summary>
    void ResumeClock();

    /// <summary>Forgets everything this freeze kept.</summary>
    void ClearLedger();
}

/// <summary>The unfreeze ORDER, kept pure so it is pinned (regression <c>freeze_unfreeze_order_*</c>; time-pause rewrite
/// 2026-10-02): the held animation requests are replayed and the hold is released while the clock is still stopped (each
/// model takes the state the game last asked for and snaps to its logical pose on a paused frame, so nothing visibly jumps
/// after the world runs), THEN the clock runs. Every step runs through
/// <see cref="FreezeStepRunner"/>, so one throwing step never skips the rest — above all, a throwing hold release can never
/// leave the game paused. Returns the first exception.</summary>
internal static class FreezeTeardown
{
    public static Exception? Run(IFreezeTeardownSteps steps) =>
        FreezeStepRunner.RunAll(steps.ReleaseAnim, steps.StopHold, steps.ResumeClock, steps.ClearLedger);

    /// <summary>The whole unfreeze: the deferred removals are replayed FIRST (each entity released from the hold, then removed
    /// through the game's own call), then the removal queue is disarmed, then <see cref="Run"/>. All through
    /// <see cref="FreezeStepRunner"/>: a throwing flush never skips the disarm or the teardown, so the scene is never left
    /// frozen (review 2026-10-02). Returns the first exception.</summary>
    public static Exception? RunAfterFlush(Action flushDeferred, Action disarmRemovals, IFreezeTeardownSteps steps)
    {
        var first = FreezeStepRunner.RunAll(flushDeferred, disarmRemovals);
        var rest = Run(steps);
        return first ?? rest;
    }
}
