using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Application.Tests.Posing;
using Xunit;
using static Stellar.Application.Tests.FreeCamera.CameraOverrideServiceTests;

namespace Stellar.Application.Tests.FreeCamera;

// Task 10 fix round 1: the Game.OnLeaveScene PREFIX and the SceneChanged(null) backstop both route to
// FreeCameraReleaser.Release(SceneChanged). Pins: the leave hands back camera + freeze (with the backend's normal
// unfreeze) and keeps/re-asserts the shield; a second call is a no-op; one throwing step never skips the next.
// Scene-stays spec § 2: the same call also resets every posed person — posing ends on exactly the freeze's reasons.
public sealed class FreeCameraReleaserTests
{
    internal sealed class CountingShieldBackend : IInputShieldBackend
    {
        public readonly List<bool> Calls = new();
        public bool Apply(bool camera, bool pause) { Calls.Add(camera); return true; }
        public void Forget() { }
    }

    internal sealed class CountingFreezeBackend : ISceneFreezeBackend
    {
        public int Freezes, Unfreezes;
        public event Action? HoldDisabled { add { } remove { } }
        public void EnsureHooks() { }
        public void FreezeAll(bool holdPositions) => Freezes++;
        public void UnfreezeAll() => Unfreezes++;
        public bool HoldsPositions => false;
    }

    internal sealed class NullReader : IShieldInputReader
    {
        public bool IsHeld(StellarKeyCode key) => false;
        public ModifierKeys Modifiers => default;
        public bool IsMouseHeld(int button) => false;
        public (float X, float Y) MouseDelta => (0, 0);
        public float Wheel => 0;
        public (float X, float Y) Pointer => (0, 0);
    }

    internal sealed class NoFocus : ITextFieldFocus
    {
        public bool AnyFieldFocused => false;
    }

    private sealed record Rig(FreeCameraReleaser Releaser, CameraOverrideService Camera, FakeBackend CamBackend,
        InputShieldService Shield, CountingShieldBackend ShieldBackend, SceneFreezeService Freeze,
        CountingFreezeBackend FreezeBackend, PosingService Posing, FakePosingBackend PosingBackend, List<string> Warn);

    private static Rig Make()
    {
        var warn = new List<string>();
        var camBackend = new FakeBackend();
        var camera = new CameraOverrideService(camBackend, new LookAtService(new FakeLookAt(), warn.Add), false, warn.Add);
        var shieldBackend = new CountingShieldBackend();
        var shield = new InputShieldService(shieldBackend, new NullReader(), new NoFocus(), warn.Add);
        var freezeBackend = new CountingFreezeBackend();
        var freeze = new SceneFreezeService(freezeBackend, false);
        var posingBackend = new FakePosingBackend();
        var posing = new PosingService(posingBackend, () => true, freeze, warn.Add);
        return new Rig(new FreeCameraReleaser(camera, posing, freeze, shield, warn.Add), camera, camBackend, shield, shieldBackend,
            freeze, freezeBackend, posing, posingBackend, warn);
    }

    [Fact]
    public void Scene_leave_releases_camera_and_freeze_and_reasserts_a_held_shield()
    {
        var r = Make();
        r.Camera.TryAcquire(out var control);
        var handle = r.Shield.Shield();
        r.Freeze.Freeze();
        var reasons = new List<CameraReleaseReason>();
        r.Camera.Released += reasons.Add;

        r.Releaser.Release(CameraReleaseReason.SceneChanged);

        Assert.False(control!.IsActive);
        Assert.Equal(1, r.CamBackend.Ends);
        Assert.Equal(new[] { CameraReleaseReason.SceneChanged }, reasons);
        Assert.Equal(1, r.FreezeBackend.Unfreezes);
        Assert.False(r.Freeze.IsFrozen);
        Assert.True(handle.IsActive);                                  // zone change keeps the plugin's shield handle
        Assert.Equal(new[] { true, true }, r.ShieldBackend.Calls);     // acquire + re-assert, never lowered
    }

    [Fact]
    public void Second_release_is_a_no_op_so_the_prefix_and_the_SceneChanged_backstop_can_both_fire()
    {
        var r = Make();
        r.Camera.TryAcquire(out _);
        r.Freeze.Freeze();
        var released = 0;
        r.Camera.Released += _ => released++;

        r.Releaser.Release(CameraReleaseReason.SceneChanged);   // Game.OnLeaveScene prefix
        r.Releaser.Release(CameraReleaseReason.SceneChanged);   // SceneChanged(null) from the postfix
        r.Releaser.Release(CameraReleaseReason.SceneChanged);   // SceneChanged(name) from OnEnterScene

        Assert.Equal(1, released);
        Assert.Equal(1, r.CamBackend.Ends);
        Assert.Equal(1, r.FreezeBackend.Unfreezes);
        Assert.Empty(r.ShieldBackend.Calls);                    // nothing held: no backend call at all
    }

    [Fact]
    public void Nothing_held_touches_no_backend()
    {
        var r = Make();
        var changed = 0;
        r.Posing.Changed += () => changed++;
        r.Releaser.Release(CameraReleaseReason.SceneChanged);
        r.Releaser.Release(CameraReleaseReason.Disconnected);
        Assert.Equal(0, r.CamBackend.Ends);
        Assert.Equal(0, r.FreezeBackend.Unfreezes);
        Assert.Empty(r.ShieldBackend.Calls);
        Assert.Empty(r.Warn);
        Assert.Equal(0, changed);   // nobody posed: no Changed
    }

    [Fact]
    public void Disconnect_also_releases_the_shield()
    {
        var r = Make();
        var handle = r.Shield.Shield();
        r.Releaser.Release(CameraReleaseReason.Disconnected);
        Assert.False(handle.IsActive);
        Assert.Equal(new[] { true, false }, r.ShieldBackend.Calls);
    }

    [Fact]
    public void A_throwing_Released_handler_never_skips_the_freeze_release_and_warns_once()
    {
        var r = Make();
        r.Camera.Released += _ => throw new InvalidOperationException("plugin bug");
        r.Camera.TryAcquire(out _);
        r.Freeze.Freeze();

        r.Releaser.Release(CameraReleaseReason.SceneChanged);

        Assert.Equal(1, r.FreezeBackend.Unfreezes);
        Assert.Single(r.Warn, w => w.Contains("plugin bug"));
    }

    // Scene-stays spec § 2: every reason that ends the freeze also ends posing, from this one call — with or without a
    // free camera held, and in the release order camera → posing → freeze → shield.
    [Theory]
    [InlineData(CameraReleaseReason.SceneChanged)]
    [InlineData(CameraReleaseReason.Cutscene)]
    [InlineData(CameraReleaseReason.GamePhotoMode)]
    [InlineData(CameraReleaseReason.Disconnected)]
    [InlineData(CameraReleaseReason.PluginUnloaded)]
    public void Every_scene_end_reason_releases_the_freeze_and_every_pose_together(CameraReleaseReason reason)
    {
        var r = Make();
        r.Freeze.Freeze();
        var copy = r.Posing.Select(new EntityId(2))!;
        copy.Yaw = 5f;
        Assert.Equal(new[] { "yaw 5", "frozen True" }, r.PosingBackend.Model(2).Calls);

        r.Releaser.Release(reason);

        Assert.Equal(PoseTargetState.Released, copy.State);
        Assert.False(r.Posing.HasTargets);
        Assert.False(r.Freeze.IsFrozen);
        Assert.Equal(1, r.FreezeBackend.Unfreezes);
        Assert.Equal(new[] { "yaw 5", "frozen True", "frozen False", "close Yaw" }, r.PosingBackend.Model(2).Calls);
    }

    // Scene-stays spec § 1: the plugin's own free-camera exit (its handle disposed) is not a scene end — the freeze and
    // every pose stay; only the framework's forced release (this releaser) ends them.
    [Fact]
    public void scene_stays_camera_release_keeps_freeze_and_poses()
    {
        var r = Make();
        r.Camera.TryAcquire(out var control);
        r.Freeze.Freeze();
        var copy = r.Posing.Select(new EntityId(2))!;
        copy.Yaw = 5f;

        control!.Dispose();

        Assert.True(r.Freeze.IsFrozen);
        Assert.Equal(0, r.FreezeBackend.Unfreezes);
        Assert.Equal(PoseTargetState.Ready, copy.State);
        Assert.Null(r.PosingBackend.Model(2).Closed);
    }

    [Fact]
    public void A_throwing_posing_Changed_handler_never_skips_the_freeze_release()
    {
        var r = Make();
        r.Freeze.Freeze();
        r.Posing.Select(new EntityId(2))!.Yaw = 5f;
        r.Posing.Changed += () => throw new InvalidOperationException("plugin handler");

        r.Releaser.Release(CameraReleaseReason.Cutscene);

        Assert.Equal(1, r.FreezeBackend.Unfreezes);
        Assert.False(r.Posing.HasTargets);
    }
}
