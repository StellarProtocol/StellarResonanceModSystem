using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Photo Studio fw fix round (spec § 6 "framework backstop" / qa M6): a plugin unloaded while its look is live must
// not leave the game re-graded, and must never be called back through a photo-mode event it subscribed to.
public sealed class PluginPhotoFacadesTests
{
    private sealed class FakeBackend : ILookBackend
    {
        public LookCapabilities Capabilities => new((LookGroups)127);
        public readonly List<LookSettings?> Applied = new();
        public void Apply(LookSettings? settings) => Applied.Add(settings);
        public void UpdateFocus(float distance) { }
        public float? MeasureFocusDistance() => null;
    }

    private sealed class FakeProbe : IPhotoModeProbe
    {
        public event Action<PhotoModeKind>? KindChanged;
        public event Action<bool>? CutsceneChanged;
        public void Raise(PhotoModeKind k) => KindChanged?.Invoke(k);
        public void Cut(bool v) => CutsceneChanged?.Invoke(v);
    }

    private static readonly LookSettings Look = new() { Color = new ColorLook { Saturation = -10 } };

    [Fact]
    public void Release_disposes_the_plugins_live_look()
    {
        var b = new FakeBackend();
        var svc = new RenderLookService(b, _ => { });
        var p = new PluginRenderLook(svc);
        var h = p.Apply(Look);
        p.ReleaseAll();
        Assert.False(h.IsActive);
        Assert.Null(b.Applied[^1]);
    }

    [Fact]
    public void Release_never_turns_off_another_plugins_look_that_replaced_this_one()
    {
        var b = new FakeBackend();
        var svc = new RenderLookService(b, _ => { });
        var a = new PluginRenderLook(svc);
        var other = new PluginRenderLook(svc);
        a.Apply(Look);
        var winner = other.Apply(Look with { Color = new ColorLook() });
        var applies = b.Applied.Count;
        a.ReleaseAll();
        Assert.True(winner.IsActive);
        Assert.Equal(applies, b.Applied.Count);
    }

    [Fact]
    public void Handles_the_plugin_already_disposed_are_not_kept()
    {
        var svc = new RenderLookService(new FakeBackend(), _ => { });
        var p = new PluginRenderLook(svc);
        for (var i = 0; i < 50; i++) p.Apply(Look).Dispose();
        Assert.True(p.TrackedCount <= 1, $"tracked {p.TrackedCount}");
    }

    [Fact]
    public void Capabilities_pass_through() =>
        Assert.Equal((LookGroups)127, new PluginRenderLook(new RenderLookService(new FakeBackend(), _ => { })).Capabilities.Supported);

    [Fact]
    public void Release_drops_every_photo_mode_handler_added_through_the_facade()
    {
        var probe = new FakeProbe();
        var state = new PhotoModeService(probe);
        var p = new PluginPhotoModeState(state);
        var calls = 0;
        p.Entered += _ => calls++;
        p.Exited += () => calls++;
        p.CutsceneChanged += _ => calls++;
        probe.Raise(PhotoModeKind.Selfie);
        Assert.Equal(1, calls);
        p.ReleaseAll();
        probe.Raise(PhotoModeKind.None);
        probe.Cut(true);
        probe.Raise(PhotoModeKind.CameraFrame);
        Assert.Equal(1, calls);
        Assert.True(p.IsActive);                 // state reads still pass through
        Assert.Equal(PhotoModeKind.CameraFrame, p.Kind);
        Assert.True(p.InCutscene);
    }

    [Fact]
    public void Remove_detaches_a_photo_mode_handler()
    {
        var probe = new FakeProbe();
        var p = new PluginPhotoModeState(new PhotoModeService(probe));
        var calls = 0;
        void H(bool _) => calls++;
        p.CutsceneChanged += H;
        p.CutsceneChanged -= H;
        probe.Cut(true);
        Assert.Equal(0, calls);
    }
}
