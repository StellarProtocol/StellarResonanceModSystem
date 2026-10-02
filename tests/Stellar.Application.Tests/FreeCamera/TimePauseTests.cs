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

// The scene freeze is a GLOBAL TIME PAUSE (Photo Studio scene-stays spec, amendment 2026-10-02 late; devkit recon
// free-camera-recon.md § Run 9, probe probe/time-pause@c280d10): Time.timeScale = 0 stops animation, skills mid-cast and
// effects, which the per-entity freeze could not (skill timelines run on scaled time). Pinned, each through its pure rule
// (ClockPauseState / FreezeTeardown / the real release path):
//  1. pause → restore ordering: the value found at the press is put back; a new pause starts a fresh book;
//  2. the hold against the game's own writes (hit-stop's OnStop writes 1.0): held while paused, the latest kept as the wish
//     and restored; a write of 0 runs and is no wish; never restore 0 or less (the game would stay paused);
//  3. every scene-end reason (FreeCameraReleaser) resumes the clock; a plain free-camera release keeps the world paused
//     (owner: "Stay frozen");
//  4. the watchdog: a pause its freeze lost resumes, a pause outside the world releases the scene, a drift behind the hook
//     is re-asserted, a stalled framework tick resumes on its own.
// Do not weaken.
public sealed class TimePauseTests
{
    // ---- 1. pause / restore ordering ----

    [Fact]
    public void time_pause_restores_the_value_found_at_the_press()
    {
        var s = new ClockPauseState();
        Assert.Null(s.End());                                    // not paused: nothing to restore
        Assert.True(s.Begin(current: 1.25f, now: 0f));
        Assert.True(s.Paused);
        Assert.Equal(1.25f, s.Saved);
        Assert.False(s.Begin(current: 0f, now: 1f));             // already paused: the first saved value stands
        Assert.Equal(1.25f, s.Saved);
        Assert.Equal(1.25f, s.End());
        Assert.False(s.Paused);
        Assert.Null(s.End());                                    // a second resume writes nothing
    }

    [Fact]
    public void time_pause_a_new_pause_starts_a_fresh_book()
    {
        var s = new ClockPauseState();
        s.Begin(1f, 0f);
        Assert.False(s.NoteGameWrite(0.5f));
        s.Verify(0.7f);
        s.End();
        s.Begin(1f, 10f);
        Assert.Null(s.Wanted);
        Assert.Equal(0, s.HeldWrites);
        Assert.Equal(0, s.Bypassed);
        Assert.Equal(1f, s.End());
    }

    // ---- 2. the hold against the game's own writes ----

    [Fact]
    public void time_pause_holds_the_games_writes_and_restores_the_latest_wish()
    {
        var s = new ClockPauseState();
        Assert.True(s.NoteGameWrite(0.05f));                     // not paused: every write runs
        s.Begin(current: 0.05f, now: 0f);                        // paused mid hit-stop
        Assert.True(s.NoteGameWrite(0f));                        // a write of 0 keeps the pause: it runs …
        Assert.Null(s.Wanted);                                   // … and is no wish
        Assert.False(s.NoteGameWrite(0.2f));                     // slow motion: held
        Assert.False(s.NoteGameWrite(1f));                       // ZTimeScaleShowInfo.OnStop writes 1.0: held
        Assert.Equal(2, s.HeldWrites);
        Assert.Equal(1f, s.Wanted);
        Assert.Equal(1f, s.End());                               // the hit-stop ended while paused: resume at 1.0, not 0.05
        Assert.True(s.NoteGameWrite(0.3f));                      // running again: the game writes freely
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void time_pause_never_restores_a_stopped_clock(float saved)
    {
        var s = new ClockPauseState();
        s.Begin(saved, 0f);
        Assert.Equal(ClockPauseState.Running, s.End());
    }

    // ---- 3. every scene-end reason resumes the clock; a camera release keeps it paused ----

    [Theory]
    [InlineData(CameraReleaseReason.SceneChanged)]
    [InlineData(CameraReleaseReason.Cutscene)]
    [InlineData(CameraReleaseReason.GamePhotoMode)]
    [InlineData(CameraReleaseReason.Disconnected)]
    [InlineData(CameraReleaseReason.PluginUnloaded)]
    public void time_pause_every_scene_end_reason_resumes_the_clock(CameraReleaseReason reason)
    {
        var r = Make();
        r.Freeze.Freeze();
        r.Freeze.Freeze();                                       // two holders: one pause
        Assert.True(r.Clock.Paused);
        Assert.Equal(1, r.Backend.Pauses);

        r.Releaser.Release(reason);

        Assert.False(r.Clock.Paused);
        Assert.False(r.Freeze.IsFrozen);
        Assert.Equal(new[] { 1.5f }, r.Backend.Restored);        // the saved value, written back once
    }

    [Fact]
    public void time_pause_a_free_camera_exit_keeps_the_world_paused_until_unfreeze()
    {
        var r = Make();
        r.Camera.TryAcquire(out var control);
        var token = r.Freeze.Freeze();

        control!.Dispose();                                      // Esc out of the free camera while frozen

        Assert.True(r.Clock.Paused);                             // owner: "Stay frozen"
        token.Dispose();                                         // the Scene group's Unfreeze
        Assert.False(r.Clock.Paused);
        Assert.Equal(new[] { 1.5f }, r.Backend.Restored);
    }

    // ---- 4. the watchdog ----

    [Fact]
    public void time_pause_watchdog_verdicts_in_order()
    {
        var s = new ClockPauseState();
        Assert.Equal(ClockPauseState.WatchVerdict.Ok, s.Watch(holderFrozen: false, worldActive: false, observed: 1f, now: 0f));   // not paused
        s.Begin(1f, 0f);
        Assert.Equal(ClockPauseState.WatchVerdict.Ok, s.Watch(true, true, 0f, 1f));
        Assert.Equal(ClockPauseState.WatchVerdict.LostPause, s.Watch(false, false, 1f, 2f));   // lost beats left-world
        Assert.Equal(ClockPauseState.WatchVerdict.LeftWorld, s.Watch(true, false, 1f, 3f));
        Assert.Equal(0, s.Bypassed);                                                             // neither counted a drift
        Assert.Equal(ClockPauseState.WatchVerdict.Reasserted, s.Watch(true, true, 0.8f, 4f));   // set behind the hook
        Assert.Equal(1, s.Bypassed);
        Assert.Equal(0.8f, s.Wanted);                                                           // the drift is the wish
        Assert.Equal(0.8f, s.End());
    }

    [Fact]
    public void time_pause_watchdog_resumes_when_the_framework_tick_stalls()
    {
        var s = new ClockPauseState();
        Assert.False(s.Stalled(100f));                           // not paused: never stalled
        s.Begin(1f, now: 0f);
        Assert.False(s.Stalled(ClockPauseState.StallSeconds));   // exactly the limit: still fine
        Assert.True(s.Stalled(ClockPauseState.StallSeconds + 0.1f));
        s.Watch(true, true, 0f, now: 9f);                        // a tick beat
        Assert.False(s.Stalled(18f));
        Assert.True(s.Stalled(19.5f));
        s.End();
        Assert.False(s.Stalled(1000f));
    }

    // ---- the position hold: remote movers yes, never the local player or their mount ----

    [Fact]
    public void time_pause_position_hold_pins_remote_movers_only()
    {
        var l = new FreezeLedger();
        l.Begin(self: 42);
        l.Exclude(99);                                           // the mount they ride
        Assert.True(FreezeTargets.MayHoldAny(l));
        Assert.True(FreezeTargets.MayHold(l, 7, FreezeKinds.Char, 30f, false));       // another player walking (R9-2)
        Assert.True(FreezeTargets.MayHold(l, 8, FreezeKinds.Monster, 30f, false));    // a monster
        Assert.False(FreezeTargets.MayHold(l, 42, FreezeKinds.Char, 0f, false));      // never the local player
        Assert.False(FreezeTargets.MayHold(l, 99, FreezeKinds.Vehicle, 0f, false));   // nor their mount
        Assert.False(FreezeTargets.MayHold(l, 9, FreezeKinds.Bullet, 1f, false));     // an effect: the pause covers it
    }

    // ---- rig: the real releaser + service + teardown + book, a backend that pauses like GameFreezeBackend ----

    private sealed class PausingBackend : ISceneFreezeBackend, IFreezeTeardownSteps
    {
        private readonly ClockPauseState _clock;
        public int Pauses;
        public readonly List<float> Restored = new();

        public PausingBackend(ClockPauseState clock) => _clock = clock;

        public event Action? HoldDisabled { add { } remove { } }
        public bool HoldsPositions => false;
        public void EnsureHooks() { }

        public void FreezeAll(bool holdPositions)
        {
            if (_clock.Begin(1.5f, 0f)) Pauses++;
        }

        public void UnfreezeAll() => FreezeTeardown.RunAfterFlush(() => { }, () => { }, this);

        public void ReleaseAnim() { }
        public void StopHold() { }
        public void ResumeClock() { if (_clock.End() is float v) Restored.Add(v); }
        public void ClearLedger() { }
    }

    private sealed record Rig(FreeCameraReleaser Releaser, CameraOverrideService Camera, SceneFreezeService Freeze,
        PausingBackend Backend, ClockPauseState Clock);

    private static Rig Make()
    {
        var warn = new List<string>();
        var clock = new ClockPauseState();
        var backend = new PausingBackend(clock);
        var camera = new CameraOverrideService(new FakeBackend(), new LookAtService(new FakeLookAt(), warn.Add), false, warn.Add);
        var shield = new InputShieldService(new CountingShieldBackend(), new NullReader(), new NoFocus(), warn.Add);
        var freeze = new SceneFreezeService(backend, false);
        var posing = new PosingService(new FakePosingBackend(), () => true, freeze, warn.Add);
        return new Rig(new FreeCameraReleaser(camera, posing, freeze, shield, warn.Add), camera, freeze, backend, clock);
    }
}
