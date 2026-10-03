using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

/// <summary>The clock side the watchdog drives (implemented by <see cref="GameClockPause"/>; faked over
/// <see cref="ClockPauseState"/> in unit tests).</summary>
internal interface ITimePauseClock
{
    /// <summary>True while the game's clock is held at 0.</summary>
    bool IsPaused { get; }

    /// <summary>The per-tick check (beats; re-asserts 0 on a drift): see <see cref="ClockPauseState.Watch"/>.</summary>
    ClockPauseState.WatchVerdict Watch(bool holderFrozen, bool worldActive);

    /// <summary>The per-paused-frame stall check: see <see cref="ClockPauseState.Stalled"/>.</summary>
    bool Stalled();

    /// <summary>Runs the clock again (no-op when not paused).</summary>
    void Resume();
}

/// <summary>The scene freeze's time-pause watchdog (qa I-1 / perf major 2, 2026-10-03). <see cref="Tick"/> runs at the
/// framework's global rate OUTSIDE the world gate — beside the login and loading-screen probes, never on the world-gated
/// <c>IFramework.Update</c> multicast, which stops exactly when the world is gone (so "paused outside the world" could never
/// fire) and which any throwing subscriber could starve. Every way a pause is found lost ends in a FULL release through the
/// framework's own release path (<c>FreeCameraReleaser</c>: camera, posing, every freeze token → the backend's teardown:
/// animation requests replayed and the gate disarmed, the position hold stopped, the clock resumed, then the input shield),
/// never a bare clock resume that would leave the backend frozen — the gate armed on pooled controllers and the hold running
/// into the next world:
/// <list type="bullet">
/// <item>paused outside the world (a scene end the release path missed) → released as a zone change;</item>
/// <item>the framework tick stalled (<see cref="AfterPausedFrame"/>, checked after each paused frame's tick) → released as an
/// error;</item>
/// <item>the paused-frame driver destroyed (<see cref="DriverGone"/>) → released as an error.</item>
/// </list>
/// A pause the freeze no longer holds (its bookkeeping already unfroze) only needs the clock back. The clock is resumed after
/// every release whatever the release did. Main thread.</summary>
internal sealed class TimePauseWatchdog
{
    private readonly ITimePauseClock _clock;
    private readonly Func<bool> _frozen;
    private readonly Func<bool> _worldActive;
    private readonly Action<CameraReleaseReason> _release;
    private readonly Action<string> _warn;

    public TimePauseWatchdog(ITimePauseClock clock, Func<bool> frozen, Func<bool> worldActive, Action<CameraReleaseReason> release,
        Action<string> warn)
    {
        _clock = clock;
        _frozen = frozen;
        _worldActive = worldActive;
        _release = release;
        _warn = warn;
    }

    /// <summary>Full releases this watchdog made this session (diagnostics).</summary>
    public int Releases { get; private set; }

    /// <summary>The global-rate check (outside the world gate). Cheap when not paused: one field read.</summary>
    public void Tick()
    {
        if (!_clock.IsPaused) return;
        switch (_clock.Watch(_frozen(), _worldActive()))
        {
            case ClockPauseState.WatchVerdict.LostPause:
                _warn("watchdog: the time pause outlived its freeze; resuming the game");
                _clock.Resume();
                break;
            case ClockPauseState.WatchVerdict.LeftWorld:
                Release(CameraReleaseReason.SceneChanged, "paused outside the world");
                break;
        }
    }

    /// <summary>After every paused frame's tick (the unscaled driver): a persisting stall releases the freeze.</summary>
    public void AfterPausedFrame()
    {
        if (_clock.IsPaused && _clock.Stalled())
            Release(CameraReleaseReason.Error, $"no framework tick for {ClockPauseState.StallSeconds:F0} s while paused");
    }

    /// <summary>The paused-frame driver was destroyed while paused: nothing would watch the pause any more.</summary>
    public void DriverGone()
    {
        if (_clock.IsPaused) Release(CameraReleaseReason.Error, "the paused-frame driver is gone");
    }

    private void Release(CameraReleaseReason reason, string why)
    {
        Releases++;
        _warn("watchdog: " + why + "; releasing the scene freeze");
        try { _release(reason); }
        catch (Exception ex) { _warn("watchdog: the release threw: " + ex.Message); }
        finally { _clock.Resume(); }   // never leave the game paused, whatever the release did
    }
}
