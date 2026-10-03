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
        private float _focal = 20f;
        private LensGateFit _fit = LensGateFit.Horizontal;
        public bool Auto = true;
        public int AspectWrites, FovWrites, Resets, FitWrites, FocalWrites;
        public float ScreenAspect = 16f / 9f;
        public bool Physical;
        public bool ThrowOnFov, ThrowOnFocal, ThrowOnFocalRestore;
        public float Aspect { get => Auto ? ScreenAspect : _aspect; set { _aspect = value; Auto = false; AspectWrites++; } }
        public float FieldOfView
        {
            get => _fov;
            set { if (ThrowOnFov) throw new System.InvalidOperationException("fov"); _fov = value; FovWrites++; }
        }
        public bool UsePhysicalProperties => Physical;
        public LensGateFit GateFit { get => _fit; set { _fit = value; FitWrites++; } }
        public float FocalLength
        {
            get => _focal;
            set
            {
                if (ThrowOnFocal || (ThrowOnFocalRestore && value == 20f)) throw new System.InvalidOperationException("focal");
                _focal = value;
                FocalWrites++;
            }
        }
        public float SensorWidth => 36f;
        public float SensorHeight => 24f;
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

    // ── review I-3: a PHYSICAL camera (Panda.Script sets usePhysicalProperties + gate fit) ─────────────────────────────
    // Sensor 36 × 24 mm, focal 20 mm, Horizontal gate fit, 16:9 window: on screen the horizontal half-tangent is
    // 36 / 40 = 0.9, so the vertical one is 0.9 / (16/9) = 0.50625 (53.70°) — NOT the sensor-height angle (61.93°).

    private static FakeLens PhysicalLens() => new() { Physical = true };

    [Fact]
    public void Physical_portrait_keeps_the_on_screen_vertical_angle_through_an_explicit_vertical_gate_fit()
    {
        var lens = PhysicalLens();
        var o = CaptureLensOverride.Apply(lens, Shaped(2160, 3840), 1920, 1080)!;
        Assert.Equal(LensGateFit.Vertical, lens.GateFit);
        Assert.Equal(24d / (2d * 0.50625d), lens.FocalLength, 3);   // 23.704 mm: sensor height ↔ the same 53.70°
        Assert.Equal(53.70f, o.Plan.VerticalAngle, 2);
        Assert.Equal(0, lens.FovWrites);                              // fieldOfView would move the focal length
        o.Dispose();
        Assert.Equal(LensGateFit.Horizontal, lens.GateFit);
        Assert.Equal(20f, lens.FocalLength);
        Assert.True(lens.Auto);
        Assert.Equal(0, lens.FovWrites);
    }

    [Fact]
    public void Physical_wide_keeps_the_on_screen_horizontal_angle()
    {
        var lens = PhysicalLens();
        var o = CaptureLensOverride.Apply(lens, Shaped(3840, 1646), 1920, 1080)!;
        var renderHalfTanV = 24d / (2d * lens.FocalLength);
        Assert.Equal(0.9d, renderHalfTanV * (3840d / 1646d), 4);    // the render's horizontal half-tangent = the screen's
        Assert.Equal(LensGateFit.Vertical, lens.GateFit);
        o.Dispose();
        Assert.Equal(20f, lens.FocalLength);
        Assert.Equal(LensGateFit.Horizontal, lens.GateFit);
    }

    [Fact]
    public void Physical_vertical_fit_portrait_changes_only_the_aspect()
    {
        var lens = PhysicalLens();
        lens.GateFit = LensGateFit.Vertical;
        lens.FitWrites = 0;
        CaptureLensOverride.Apply(lens, Shaped(2160, 3840), 1920, 1080)!.Dispose();
        Assert.Equal(0, lens.FitWrites + lens.FocalWrites + lens.FovWrites);
        Assert.Equal(1, lens.Resets);
    }

    [Theory]
    [InlineData(LensGateFit.Vertical, 16d / 9d, LensGateFit.Vertical)]
    [InlineData(LensGateFit.Horizontal, 16d / 9d, LensGateFit.Horizontal)]
    [InlineData(LensGateFit.None, 16d / 9d, LensGateFit.Vertical)]
    [InlineData(LensGateFit.Fill, 16d / 9d, LensGateFit.Horizontal)]     // wider than the 3:2 sensor → crop to its width
    [InlineData(LensGateFit.Fill, 1.2d, LensGateFit.Vertical)]
    [InlineData(LensGateFit.Overscan, 16d / 9d, LensGateFit.Vertical)]
    [InlineData(LensGateFit.Overscan, 1.2d, LensGateFit.Horizontal)]
    public void Gate_fit_resolves_like_unity(object fit, double view, object expected) =>
        Assert.Equal((LensGateFit)expected, CaptureLensPlanner.ResolveFit((LensGateFit)fit, view, 1.5d));

    [Fact]
    public void A_failed_angle_write_undoes_the_aspect()
    {
        var lens = new FakeLens { ThrowOnFov = true };
        Assert.Throws<System.InvalidOperationException>(() => CaptureLensOverride.Apply(lens, Shaped(3840, 1646), 1920, 1080));
        Assert.True(lens.Auto);
        Assert.Equal(1, lens.Resets);
    }

    [Fact]
    public void A_failed_focal_write_undoes_the_gate_fit_and_the_aspect()
    {
        var lens = new FakeLens { Physical = true, ThrowOnFocal = true };
        Assert.Throws<System.InvalidOperationException>(() => CaptureLensOverride.Apply(lens, Shaped(2160, 3840), 1920, 1080));
        Assert.Equal(LensGateFit.Horizontal, lens.GateFit);
        Assert.True(lens.Auto);
    }

    [Fact]
    public void A_throwing_restore_step_still_runs_the_others_once_and_reports_the_failure()
    {
        var lens = new FakeLens { Physical = true, ThrowOnFocalRestore = true };
        var o = CaptureLensOverride.Apply(lens, Shaped(2160, 3840), 1920, 1080)!;
        Assert.Throws<System.InvalidOperationException>(() => o.Dispose());
        Assert.Equal(LensGateFit.Horizontal, lens.GateFit);
        Assert.True(lens.Auto);
        o.Dispose();   // once only
        Assert.Equal(1, lens.Resets);
    }

    [Fact]
    public void Same_lens_compares_every_restored_property()
    {
        var a = new LensState(1.7778f, 45f, true, LensGateFit.Horizontal, 20f, 36f, 24f);
        Assert.True(CaptureLensPlanner.SameLens(a, a));
        Assert.False(CaptureLensPlanner.SameLens(a, a with { Aspect = 0.5625f }));
        Assert.False(CaptureLensPlanner.SameLens(a, a with { FieldOfView = 40f }));
        Assert.False(CaptureLensPlanner.SameLens(a, a with { GateFit = LensGateFit.Vertical }));
        Assert.False(CaptureLensPlanner.SameLens(a, a with { FocalLength = 23.7f }));
        Assert.False(CaptureLensPlanner.SameLens(a, a with { Physical = false }));
    }
}
