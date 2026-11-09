using Stellar.Infrastructure.Unity;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>Regression (2026-11-09, 71728ac log): one slow drag-scroll of the launcher grid flipped pixelPerfect 17 times,
/// because every pause &gt; 0.15 s between drag steps (button still held) let it settle back ON. Now the first real move of
/// a hold keeps the list "moving" until release; after release only the glide counts. Window move / resize use the same
/// rule with gliding = false (they replaced the old "sharp again 0.15 s after holding still mid-drag" behaviour).</summary>
public sealed class HeldMotionRuleTests
{
    // Feeds frames (pressed, moved, gliding) through one view's state; returns what each frame counted as.
    private static bool[] Run(params (bool pressed, bool moved, bool gliding)[] frames)
    {
        var engaged = false;
        var r = new bool[frames.Length];
        for (var i = 0; i < frames.Length; i++)
            r[i] = HeldMotionRule.CountsAsMotion(frames[i].pressed, frames[i].moved, frames[i].gliding, ref engaged);
        return r;
    }

    [Fact]
    public void PressWithoutMovementNeverCounts() =>
        Assert.Equal(new[] { false, false, false, false },
            Run((true, false, false), (true, false, false), (true, false, false), (false, false, false)));

    [Fact]
    public void FirstMoveEngagesAndPausesStayCountedUntilRelease() =>
        Assert.Equal(new[] { false, true, true, true, true, true, false },
            Run((true, false, false),   // press: nothing
                (true, true, false),    // first move → OFF
                (true, false, false),   // pause (held) — still counted, no re-enable
                (true, false, false),
                (true, true, false),    // next step
                (true, false, false),
                (false, false, false))); // released, no glide → settle starts

    [Fact]
    public void ReleaseWithGlideCountsOnlyWhileGliding() =>
        Assert.Equal(new[] { true, true, true, false },
            Run((true, true, false),
                (false, true, true),    // gliding after release
                (false, true, true),
                (false, false, false))); // glide ended / stopped by wheel

    [Fact]
    public void ReleaseClearsTheHoldSoTheNextPressStartsFresh() =>
        Assert.Equal(new[] { true, false, false },
            Run((true, true, false), (false, false, false), (true, false, false)));

    [Fact]
    public void WindowDragWithPausesStaysCountedUntilReleaseThenStops() =>   // windows never glide
        Assert.Equal(new[] { false, true, true, true, false },
            Run((true, false, false), (true, true, false), (true, false, false), (true, true, false),
                (false, false, false)));

    [Fact]
    public void UnpressedMoveWithoutGlideIsNotUserMotion() =>   // programmatic move or wheel tick
        Assert.False(Run((false, true, false))[0]);
}
