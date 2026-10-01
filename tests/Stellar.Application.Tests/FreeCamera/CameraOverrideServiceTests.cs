using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Spec § 6 ICameraOverride policy + § 7 safety: exclusive, framework-ended with a reason, 60 m hard cap from the real
// character regardless of plugin, an exception in the per-frame handler releases the camera.
public sealed class CameraOverrideServiceTests
{
    internal sealed class FakeBackend : ICameraBackend
    {
        public CameraPose? Game = new CameraPose(new Position3D(0, 2, -5), 0, 10, 0, 45);
        public Position3D? Player = new Position3D(0, 0, 0);
        public bool BeginOk = true;
        public int Begins, Ends;
        public readonly List<CameraPose> Applied = new();
        public event Action<float>? Frame;
        public CameraPose? ReadGamePose() => Game;
        public Position3D? ReadLocalPlayerPosition() => Player;
        public bool TryBegin(CameraPose start) { Begins++; return BeginOk; }
        public void Apply(CameraPose pose) => Applied.Add(pose);
        public void End() => Ends++;
        public void RaiseFrame(float dt) => Frame?.Invoke(dt);
    }

    internal sealed class FakeLookAt : ILookAtBackend
    {
        public int Applies, Restores;
        public bool TryApply() { Applies++; return true; }
        public void Restore() => Restores++;
    }

    private static (CameraOverrideService Svc, FakeBackend B, FakeLookAt L, List<string> Warn) Make(bool disabled = false)
    {
        var b = new FakeBackend();
        var l = new FakeLookAt();
        var warn = new List<string>();
        return (new CameraOverrideService(b, new LookAtService(l, warn.Add), disabled, warn.Add), b, l, warn);
    }

    private static float Dist(Position3D a, Position3D b) =>
        MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    [Fact]
    public void Second_acquirer_is_refused_until_the_first_disposes()
    {
        var (svc, b, _, _) = Make();
        Assert.True(svc.TryAcquire(out var first));
        Assert.False(svc.TryAcquire(out var second));
        Assert.Null(second);
        Assert.True(svc.IsOverridden);
        first!.Dispose();
        Assert.False(svc.IsOverridden);
        Assert.True(svc.TryAcquire(out _));
        Assert.Equal(2, b.Begins);
    }

    [Fact]
    public void Dispose_raises_Released_Disposed_once_and_ends_the_backend_once()
    {
        var (svc, b, _, _) = Make();
        var reasons = new List<CameraReleaseReason>();
        svc.Released += reasons.Add;
        svc.TryAcquire(out var c);
        c!.Dispose();
        c.Dispose();
        Assert.Equal(new[] { CameraReleaseReason.Disposed }, reasons);
        Assert.Equal(1, b.Ends);
        Assert.False(c.IsActive);
        var applied = b.Applied.Count;
        c.SetPose(new Position3D(1, 1, 1), 0, 0, 0);
        Assert.Equal(applied, b.Applied.Count);
    }

    [Fact]
    public void Pose_beyond_60_m_from_the_player_lands_on_the_60_m_sphere()
    {
        var (svc, b, _, _) = Make();
        b.Player = new Position3D(10, 0, 10);
        svc.TryAcquire(out var c);
        c!.SetPose(new Position3D(10, 0, 210), 0, 0, 0);
        var p = b.Applied[^1].Position;
        Assert.InRange(Dist(p, b.Player.Value), 59.999f, 60.001f);
        Assert.InRange(p.Z, 69.99f, 70.01f);
    }

    [Fact]
    public void Pose_inside_the_cap_is_untouched()
    {
        var (svc, b, _, _) = Make();
        svc.TryAcquire(out var c);
        c!.SetPose(new Position3D(3, 4, 5), 30, -10, 0);
        Assert.Equal(new Position3D(3, 4, 5), b.Applied[^1].Position);
        Assert.Equal(30, b.Applied[^1].Yaw);
    }

    [Fact]
    public void Unknown_player_position_keeps_the_last_accepted_position()
    {
        var (svc, b, _, _) = Make();
        svc.TryAcquire(out var c);
        c!.SetPose(new Position3D(1, 2, 3), 0, 0, 0);
        b.Player = null;
        c.SetPose(new Position3D(500, 500, 500), 0, 0, 0);
        Assert.Equal(new Position3D(1, 2, 3), b.Applied[^1].Position);
    }

    [Fact]
    public void Roll_is_clamped_to_90_degrees()
    {
        var (svc, b, _, _) = Make();
        svc.TryAcquire(out var c);
        c!.SetPose(new Position3D(0, 0, 0), 0, 0, 170);
        Assert.Equal(90, b.Applied[^1].Roll);
        c.SetPose(new Position3D(0, 0, 0), 0, 0, -170);
        Assert.Equal(-90, b.Applied[^1].Roll);
    }

    [Fact]
    public void ReleaseAll_reports_the_framework_reason()
    {
        var (svc, b, _, _) = Make();
        var reasons = new List<CameraReleaseReason>();
        svc.Released += reasons.Add;
        svc.TryAcquire(out var c);
        svc.ReleaseAll(CameraReleaseReason.SceneChanged);
        Assert.Equal(new[] { CameraReleaseReason.SceneChanged }, reasons);
        Assert.False(c!.IsActive);
        Assert.Equal(1, b.Ends);
    }

    [Fact]
    public void A_throwing_frame_handler_releases_with_Error()
    {
        var (svc, b, _, warn) = Make();
        var reasons = new List<CameraReleaseReason>();
        svc.Released += reasons.Add;
        svc.TryAcquire(out var c);
        c!.Frame += _ => throw new InvalidOperationException("boom");
        b.RaiseFrame(0.016f);
        Assert.Equal(new[] { CameraReleaseReason.Error }, reasons);
        Assert.False(svc.IsOverridden);
        Assert.Contains(warn, w => w.Contains("boom"));
    }

    [Fact]
    public void Frame_reaches_the_holder_with_delta_time()
    {
        var (svc, b, _, _) = Make();
        svc.TryAcquire(out var c);
        var got = 0f;
        c!.Frame += dt => got = dt;
        b.RaiseFrame(0.02f);
        Assert.Equal(0.02f, got);
    }

    [Fact]
    public void Kill_switch_refuses_without_touching_the_game()
    {
        var (svc, b, _, warn) = Make(disabled: true);
        Assert.False(svc.TryAcquire(out _));
        Assert.Equal(0, b.Begins);
        Assert.Single(warn);
    }

    [Fact]
    public void Begin_failure_leaves_no_holder()
    {
        var (svc, b, _, _) = Make();
        b.BeginOk = false;
        Assert.False(svc.TryAcquire(out _));
        Assert.False(svc.IsOverridden);
    }

    [Fact]
    public void Missing_game_camera_refuses()
    {
        var (svc, b, _, _) = Make();
        b.Game = null;
        Assert.False(svc.TryAcquire(out _));
        Assert.Equal(0, b.Begins);
    }

    [Fact]
    public void GamePose_and_Fov_start_from_the_game_camera()
    {
        var (svc, b, _, _) = Make();
        svc.TryAcquire(out var c);
        Assert.Equal(b.Game!.Value, c!.GamePose);
        Assert.Equal(45, c.Fov);
        c.Fov = 30;
        Assert.Equal(30, b.Applied[^1].Fov);
    }

    [Fact]
    public void ReleaseOwner_ends_only_that_owners_control()
    {
        var (svc, _, _, _) = Make();
        var a = new object();
        var reasons = new List<CameraReleaseReason>();
        svc.Released += reasons.Add;
        svc.TryAcquire(a, out var c);
        svc.ReleaseOwner(new object());
        Assert.True(c!.IsActive);
        svc.ReleaseOwner(a);
        Assert.False(c.IsActive);
        Assert.Equal(new[] { CameraReleaseReason.PluginUnloaded }, reasons);
    }

    [Fact]
    public void LookAt_applies_once_and_restores_after_the_last_handle()
    {
        var (svc, _, l, _) = Make();
        var h1 = svc.LookAtCamera();
        var h2 = svc.LookAtCamera();
        Assert.Equal(1, l.Applies);
        h1.Dispose();
        Assert.Equal(0, l.Restores);
        h2.Dispose();
        h2.Dispose();
        Assert.Equal(1, l.Restores);
    }

    [Fact]
    public void Facade_release_ends_its_control_and_drops_its_handlers()
    {
        var (svc, _, l, _) = Make();
        var facade = new PluginCameraOverride(svc, new object());
        var calls = 0;
        facade.Released += _ => calls++;
        facade.TryAcquire(out var c);
        facade.LookAtCamera();
        facade.ReleaseAll();
        Assert.False(c!.IsActive);
        Assert.Equal(1, l.Restores);
        Assert.Equal(0, calls);              // an unloading plugin is never called back, not even for its own release
        svc.TryAcquire(out var other);
        other!.Dispose();
        Assert.Equal(0, calls);
    }
}
