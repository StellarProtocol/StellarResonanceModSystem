using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Rendering;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Photo shapes spec § Framework: the camera's aspect is set for the capture render only and restored after.
public sealed class CaptureLensOverrideTests
{
    private sealed class FakeLens : ICaptureLens
    {
        private float _aspect;
        private float _fov = 45f;
        public bool Auto = true;
        public int AspectWrites, FovWrites, Resets;
        public float ScreenAspect = 16f / 9f;
        public float Aspect { get => Auto ? ScreenAspect : _aspect; set { _aspect = value; Auto = false; AspectWrites++; } }
        public float FieldOfView { get => _fov; set { _fov = value; FovWrites++; } }
        public void ResetAspect() { Auto = true; Resets++; }
    }

    private static GrabTarget Shaped(int w, int h) => new(new CaptureSize(w, h), Shaped: true);

    [Fact]
    public void Portrait_sets_the_aspect_for_the_render_keeps_the_view_angle_and_returns_to_automatic()
    {
        var lens = new FakeLens();
        var o = CaptureLensOverride.Apply(lens, Shaped(2160, 3840), 1920, 1080)!;
        Assert.Equal(0.5625f, lens.Aspect, 4);
        Assert.Equal(0, lens.FovWrites);
        o.Dispose();
        Assert.True(lens.Auto);
        Assert.Equal(16f / 9f, lens.Aspect, 4);
        Assert.Equal(0, lens.FovWrites);
    }

    [Fact]
    public void Wide_narrows_the_vertical_angle_for_the_render_and_restores_it()
    {
        var lens = new FakeLens();
        var o = CaptureLensOverride.Apply(lens, Shaped(3840, 1646), 1920, 1080)!;
        Assert.Equal(35.0f, lens.FieldOfView, 0);
        o.Dispose();
        Assert.Equal(45f, lens.FieldOfView);
        Assert.True(lens.Auto);
    }

    [Fact]
    public void A_manual_aspect_other_than_the_window_is_put_back_as_it_was()
    {
        var lens = new FakeLens();
        lens.Aspect = 2f;   // the game set its own aspect
        var o = CaptureLensOverride.Apply(lens, Shaped(1000, 1000), 1920, 1080)!;
        Assert.Equal(1f, lens.Aspect);
        o.Dispose();
        Assert.Equal(2f, lens.Aspect);
        Assert.Equal(0, lens.Resets);
    }

    [Fact]
    public void Restore_runs_once()
    {
        var lens = new FakeLens();
        var o = CaptureLensOverride.Apply(lens, Shaped(1000, 1000), 1920, 1080)!;
        o.Dispose();
        o.Dispose();
        Assert.Equal(1, lens.Resets);
    }

    [Fact]
    public void Window_shaped_grab_touches_nothing()
    {
        var lens = new FakeLens();
        Assert.Null(CaptureLensOverride.Apply(lens, new GrabTarget(new CaptureSize(3840, 2160), Shaped: false), 1920, 1080));
        Assert.Null(CaptureLensOverride.Apply(lens, Shaped(0, 0), 1920, 1080));
        Assert.Equal(0, lens.AspectWrites + lens.FovWrites + lens.Resets);
    }
}
