using Stellar.Abstractions.Domain;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Lights review 2026-10-03 (lamp markers): ICameraOverride.TryProjectToScreen — Unity's bottom-left screen space becomes the
// plugin-facing top-left pixels (IEntityPicker's space), "in front" only at a positive finite depth, false without a camera,
// and the per-plugin view delegates.
public sealed class ScreenProjectionTests
{
    [Theory]
    [InlineData(100f, 200f, 5f, 1080f, 100f, 880f, true)]
    [InlineData(960f, 540f, 0.5f, 1080f, 960f, 540f, true)]
    [InlineData(100f, 200f, -3f, 1080f, 100f, 880f, false)]   // behind the camera
    [InlineData(100f, 200f, 0f, 1080f, 100f, 880f, false)]    // on the camera plane
    public void Unity_screen_space_maps_to_top_left_pixels(float x, float y, float depth, float h, float ex, float ey, bool front)
    {
        var p = ScreenProjection.FromUnity(x, y, depth, h);
        Assert.Equal(ex, p.X);
        Assert.Equal(ey, p.Y);
        Assert.Equal(front, p.InFront);
    }

    [Fact]
    public void A_non_finite_projection_is_never_in_front()
    {
        Assert.False(ScreenProjection.FromUnity(float.NaN, 1f, 5f, 1080f).InFront);
        Assert.False(ScreenProjection.FromUnity(1f, 1f, float.PositiveInfinity, 1080f).InFront);
    }

    [Fact]
    public void The_service_projects_through_the_backend_and_refuses_without_a_camera()
    {
        var b = new CameraOverrideServiceTests.FakeBackend();
        var svc = new CameraOverrideService(b, new LookAtService(new CameraOverrideServiceTests.FakeLookAt(), _ => { }), false, _ => { });
        Assert.False(svc.TryProjectToScreen(new Position3D(1, 2, 3), out var none));
        Assert.Equal(default, none);
        b.Projected = (320f, 100f, 4f, 720f);
        Assert.True(svc.TryProjectToScreen(new Position3D(1, 2, 3), out var p));
        Assert.Equal(new ScreenPoint(320f, 620f, true), p);
        var plugin = new PluginCameraOverride(svc, new object());
        Assert.True(plugin.TryProjectToScreen(new Position3D(1, 2, 3), out var q));
        Assert.Equal(p, q);
    }
}
