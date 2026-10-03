using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Application.Tests.Posing;
using Stellar.Infrastructure.Game;
using Xunit;
using static Stellar.Application.Tests.FreeCamera.CameraOverrideServiceTests;
using static Stellar.Application.Tests.FreeCamera.FreeCameraReleaserTests;

namespace Stellar.Application.Tests.FreeCamera;

// Time-pause review fixes, 2026-10-03 (framework feat/posing after 4cd6736; qa timepause-qa-review.md, perf
// timepause-perf-review.md). Pinned through the real FreeCameraReleaser + SceneFreezeService + FreezeTeardown and a backend
// that holds a real AnimRequestGate and a position-hold flag:
//  I-1 / perf major 2: a stalled framework tick or a gone paused-frame driver ends in a FULL release — the freeze released,
//      the animation gate disarmed, the hold stopped, the clock resumed — never a bare clock resume that leaves the backend
//      frozen (gate armed on pooled controllers, hold running into the next world); paused outside the world releases too.
//  I-2: a throwing ISceneFreeze.Changed handler never leaks the token, never skips the other handlers, never stops an unfreeze.
//  I-1 (multicast): one throwing IFramework.Update subscriber never starves the ones after it.
// Do not weaken.
public sealed class TimePauseWatchdogTests
{
    [Fact]
    public void time_pause_a_stalled_tick_releases_the_whole_freeze_not_just_the_clock()
    {
        var r = Make();
        r.Freeze.Freeze();
        Assert.True(r.Backend.Anim.Armed && r.Backend.Holding && r.Book.Paused);

        foreach (var now in new[] { 5f, 10.1f, 10.6f, 11.2f })
        {
            r.Clock.Now = now;
            r.Dog.AfterPausedFrame();
        }

        AssertFullyReleased(r, CameraReleaseReason.Error);
    }

    [Fact]
    public void time_pause_a_gone_paused_frame_driver_releases_the_whole_freeze()
    {
        var r = Make();
        r.Freeze.Freeze();
        r.Dog.DriverGone();
        AssertFullyReleased(r, CameraReleaseReason.Error);
    }

    [Fact]
    public void time_pause_paused_outside_the_world_releases_the_freeze()
    {
        var r = Make();
        r.Freeze.Freeze();
        r.Dog.Tick();                                           // in the world: nothing
        Assert.Empty(r.Reasons);
        r.World = false;                                        // a scene end the release path missed
        r.Dog.Tick();
        AssertFullyReleased(r, CameraReleaseReason.SceneChanged);
    }

    [Fact]
    public void time_pause_a_healthy_or_absent_pause_is_never_released()
    {
        var r = Make();
        r.Dog.Tick();
        r.Dog.AfterPausedFrame();
        r.Dog.DriverGone();                                     // not paused: the driver's teardown at unload is no release
        Assert.Empty(r.Reasons);
        r.Freeze.Freeze();
        r.Clock.Now = 9f;
        r.Dog.Tick();                                           // beats at 9 s
        r.Clock.Now = 15f;
        r.Dog.AfterPausedFrame();
        Assert.Empty(r.Reasons);
        Assert.True(r.Freeze.IsFrozen);
        Assert.Equal(0, r.Dog.Releases);
    }

    [Fact]
    public void time_pause_a_pause_its_freeze_already_dropped_only_resumes_the_clock()
    {
        var r = Make();
        r.Book.Begin(1.25f, 0f);                                // the clock paused, no freeze token holds it
        r.Dog.Tick();
        Assert.False(r.Book.Paused);
        Assert.Empty(r.Reasons);
        Assert.Contains(r.Warn, w => w.Contains("outlived its freeze"));
    }

    [Fact]
    public void time_pause_a_throwing_release_still_resumes_the_clock()
    {
        var r = Make(release: _ => throw new InvalidOperationException("boom"));
        r.Freeze.Freeze();
        r.Dog.DriverGone();
        Assert.False(r.Book.Paused);
        Assert.Contains(r.Warn, w => w.Contains("boom"));
    }

    // ---- I-2: Changed handlers ----

    [Fact]
    public void freeze_a_throwing_changed_handler_never_leaks_the_token_or_skips_the_others()
    {
        var b = new FreeCameraReleaserTests.CountingFreezeBackend();
        var warn = new List<string>();
        var s = new SceneFreezeService(b, false, warn.Add);
        var seen = new List<bool>();
        s.Changed += _ => throw new InvalidOperationException("plugin bug");
        s.Changed += seen.Add;

        var token = s.Freeze();                                 // the press still hands back its token
        Assert.True(s.IsFrozen);
        token.Dispose();                                        // so the holder can still unfreeze
        Assert.False(s.IsFrozen);
        Assert.Equal(1, b.Unfreezes);
        Assert.Equal(new[] { true, false }, seen);              // the second handler ran both times
        Assert.Single(warn);                                    // warned once
    }

    // ---- I-1: the Update multicast ----

    [Fact]
    public void framework_one_throwing_update_subscriber_never_starves_the_others()
    {
        var warn = new List<string>();
        var fw = new FrameworkService { Warn = warn.Add };
        var ticks = 0;
        Action<float> bad = _ => throw new InvalidOperationException("bad plugin");
        fw.Update += bad;
        fw.Update += _ => ticks++;
        fw.Tick(0.1f);
        fw.Tick(0.1f);
        Assert.Equal(2, ticks);
        Assert.Single(warn);                                    // once per subscriber, not per tick
        fw.Update -= bad;
        fw.Tick(0.1f);
        Assert.Equal(3, ticks);
    }

    // ---- rig ----

    private static void AssertFullyReleased(Rig r, CameraReleaseReason reason)
    {
        Assert.Equal(new[] { reason }, r.Reasons);
        Assert.False(r.Freeze.IsFrozen);                        // every token released
        Assert.False(r.Backend.Anim.Armed);                     // the animation gate disarmed (its kept requests replayed)
        Assert.False(r.Backend.Holding);                        // the position hold stopped
        Assert.False(r.Book.Paused);                            // the clock runs
        Assert.Equal(new[] { 1.5f }, r.Backend.Restored);       // through the backend's own teardown, once
        Assert.Equal(1, r.Dog.Releases);
    }

    private sealed class BookClock : ITimePauseClock
    {
        public readonly ClockPauseState Book;
        public float Now;
        public BookClock(ClockPauseState book) => Book = book;
        public bool IsPaused => Book.Paused;
        public ClockPauseState.WatchVerdict Watch(bool holderFrozen, bool worldActive) => Book.Watch(holderFrozen, worldActive, 0f, Now);
        public bool Stalled() => Book.Stalled(Now);
        public void Resume() => Book.End();
    }

    internal sealed class GateBackend : ISceneFreezeBackend, IFreezeTeardownSteps
    {
        private readonly ClockPauseState _clock;
        public readonly AnimRequestGate Anim = new();
        public bool Holding;
        public readonly List<float> Restored = new();

        public GateBackend(ClockPauseState clock) => _clock = clock;

        public event Action? HoldDisabled { add { } remove { } }
        public bool HoldsPositions => Holding;
        public void EnsureHooks() { }

        public void FreezeAll(bool holdPositions)
        {
            _clock.Begin(1.5f, 0f);
            Anim.Arm();
            Anim.Track(0x100, 7);
            Holding = holdPositions;
        }

        public void UnfreezeAll() => FreezeTeardown.RunAfterFlush(() => { }, () => { }, this);

        public void ReleaseAnim() => Anim.TakeReplay(_ => 0x100);
        public void StopHold() => Holding = false;
        public void ResumeClock() { if (_clock.End() is float v) Restored.Add(v); }
        public void ClearLedger() { }
    }

    private sealed class Rig
    {
        public required SceneFreezeService Freeze;
        public required GateBackend Backend;
        public required ClockPauseState Book;
        public required BookClock Clock;
        public required TimePauseWatchdog Dog;
        public readonly List<CameraReleaseReason> Reasons = new();
        public readonly List<string> Warn = new();
        public bool World = true;
    }

    private static Rig Make(Action<CameraReleaseReason>? release = null)
    {
        var book = new ClockPauseState();
        var backend = new GateBackend(book);
        var warn = new List<string>();
        var camera = new CameraOverrideService(new FakeBackend(), new LookAtService(new FakeLookAt(), warn.Add), false, warn.Add);
        var shield = new InputShieldService(new CountingShieldBackend(), new NullReader(), new NoFocus(), warn.Add);
        var freeze = new SceneFreezeService(backend, false, warn.Add);
        var posing = new PosingService(new FakePosingBackend(), () => true, freeze, warn.Add);
        var releaser = new FreeCameraReleaser(camera, posing, freeze, shield, warn.Add);
        var clock = new BookClock(book);
        Rig? r = null;
        var dog = new TimePauseWatchdog(clock, () => freeze.IsFrozen, () => r!.World, reason =>
        {
            r!.Reasons.Add(reason);
            (release ?? releaser.Release)(reason);
        }, m => r!.Warn.Add(m));
        r = new Rig { Freeze = freeze, Backend = backend, Book = book, Clock = clock, Dog = dog };
        return r;
    }
}
