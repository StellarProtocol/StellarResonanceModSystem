using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Stellar.Application.Tests.FreeCamera;
using Stellar.Application.Tests.Posing;
using Xunit;
using static Stellar.Application.Tests.FreeCamera.CameraOverrideServiceTests;

namespace Stellar.Application.Tests.Lights;

// Lights spec § 4: lights end on the scene's own reasons — through the same FreeCameraReleaser call that ends the freeze and
// posing (zone change / scene leave, cutscene, the game's camera mode, disconnect, framework unload) — and stay when a free
// camera is merely released. Lit posed copies are written back BEFORE posing removes them.
public sealed class LightsReleaseTests
{
    private sealed record Rig(FreeCameraReleaser Releaser, LightsService Lights, FakeLightsBackend Backend, CameraOverrideService Camera,
        PosingService Posing, FakePosingBackend PosingBackend);

    private static Rig Make()
    {
        var warn = new List<string>();
        var camera = new CameraOverrideService(new FakeBackend(), new LookAtService(new FakeLookAt(), warn.Add), false, warn.Add);
        var shield = new InputShieldService(new FreeCameraReleaserTests.CountingShieldBackend(), new FreeCameraReleaserTests.NullReader(),
            new FreeCameraReleaserTests.NoFocus(), warn.Add);
        var freeze = new SceneFreezeService(new FreeCameraReleaserTests.CountingFreezeBackend(), false);
        var posingBackend = new FakePosingBackend();
        var posing = new PosingService(posingBackend, () => true, freeze, warn.Add);
        var backend = new FakeLightsBackend();
        var lights = new LightsService(backend, () => true, posing, warn.Add, _ => { });
        return new Rig(new FreeCameraReleaser(camera, posing, freeze, shield, warn.Add, lights), lights, backend, camera, posing, posingBackend);
    }

    [Theory]
    [InlineData(CameraReleaseReason.SceneChanged)]
    [InlineData(CameraReleaseReason.Cutscene)]
    [InlineData(CameraReleaseReason.GamePhotoMode)]
    [InlineData(CameraReleaseReason.Disconnected)]
    [InlineData(CameraReleaseReason.PluginUnloaded)]
    public void Every_scene_end_reason_removes_lamps_restores_people_and_the_gate(CameraReleaseReason reason)
    {
        var r = Make();
        var you = r.Backend.AddPerson(1, "you");
        var original = you.Snapshot();
        var gateBefore = r.Backend.Volume.State;
        var plugin = new PluginLights(r.Lights, new object());
        plugin.PeopleLevel = 2f;
        plugin.AddLamp(LightsRig.Lamp());
        plugin.SetPersonLight(new EntityId(1), new PersonLight(new KeyLight(30f, 10f), null));
        var released = 0;
        plugin.Released += () => released++;

        r.Releaser.Release(reason);

        Assert.True(r.Backend.Lamps[0].Destroyed);
        Assert.False(r.Lights.GateRaised);
        Assert.Equal(gateBefore.Flags, r.Backend.Volume.State.Flags);
        Assert.Equal(gateBefore.Value, r.Backend.Volume.State.Value);
        Assert.Equal(gateBefore.Active, r.Backend.Volume.State.Active);
        LightsPeopleTests.AssertSame(original, you.Snapshot());
        Assert.Equal(1, released);
        Assert.Equal(2f, plugin.PeopleLevel);   // a setting, kept for the next scene
    }

    [Fact]
    public void A_plain_free_camera_release_keeps_the_lights()
    {
        var r = Make();
        r.Lights.AddLamp(LightsRig.Lamp());
        Assert.True(r.Camera.TryAcquire(out var control));
        control!.Dispose();
        Assert.False(r.Backend.Lamps[0].Destroyed);
        Assert.Equal(1, r.Lights.LampCount);
    }

    [Fact]
    public void A_lit_posed_copy_is_written_back_before_posing_removes_it()
    {
        var r = Make();
        var target = r.Posing.Select(new EntityId(2))!;
        target.PlayAction(9020);                                      // makes the copy
        var copyModel = r.PosingBackend.Model(2);
        var copy = r.Backend.AddPerson(2, "copy");
        copy.Live = () => copyModel.CloseCount == 0;                  // the copy dies when posing closes it
        var original = copy.Snapshot();
        r.Lights.SetPersonLight(new EntityId(2), new PersonLight(new KeyLight(0f, 0f), null));

        r.Releaser.Release(CameraReleaseReason.SceneChanged);

        Assert.Equal(1, copyModel.CloseCount);
        LightsPeopleTests.AssertSame(original, copy.Snapshot());      // written back while it still lived
    }

    [Fact]
    public void A_throwing_lights_release_never_skips_posing_or_the_freeze()
    {
        var r = Make();
        r.Lights.AddLamp(LightsRig.Lamp());
        r.Lights.Released += () => throw new InvalidOperationException("plugin handler");
        var target = r.Posing.Select(new EntityId(2))!;
        target.PlayAction(9020);
        r.Releaser.Release(CameraReleaseReason.Cutscene);
        Assert.Equal(1, r.PosingBackend.Model(2).CloseCount);
        Assert.True(r.Backend.Lamps[0].Destroyed);
    }
}
