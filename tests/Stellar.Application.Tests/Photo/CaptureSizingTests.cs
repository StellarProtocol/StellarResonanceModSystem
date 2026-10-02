using System;
using Stellar.Abstractions.Domain;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Photo Studio photo shapes (spec 2026-10-03-photo-studio-portrait-capture-design.md § Behaviour 2 + 4, owner-approved).
public sealed class CaptureSizingTests
{
    private static CaptureSize Size(int sw, int sh, int scale, int aw, int ah, int maxTex = CaptureSizing.MaxLongSide) =>
        CaptureSizing.OutputSize(sw, sh, scale, new CaptureAspect(aw, ah), maxTex);

    // The spec's table, verbatim: 2× on 1920 × 1080.
    [Theory]
    [InlineData(9, 16, 2160, 3840)]
    [InlineData(4, 5, 3072, 3840)]
    [InlineData(2, 3, 2560, 3840)]
    [InlineData(1, 1, 3840, 3840)]
    [InlineData(21, 9, 3840, 1646)]
    public void Spec_table_at_2x_on_1920x1080(int aw, int ah, int w, int h) =>
        Assert.Equal(new CaptureSize(w, h), Size(1920, 1080, 2, aw, ah));

    // "4× doubles each side."
    [Theory]
    [InlineData(9, 16)] [InlineData(4, 5)] [InlineData(2, 3)] [InlineData(1, 1)] [InlineData(21, 9)]
    public void Four_x_doubles_each_side_of_two_x(int aw, int ah)
    {
        var two = Size(1920, 1080, 2, aw, ah);
        Assert.Equal(new CaptureSize(two.Width * 2, two.Height * 2), Size(1920, 1080, 4, aw, ah));
    }

    [Fact]
    public void One_x_portrait_has_the_window_long_side() => Assert.Equal(new CaptureSize(1080, 1920), Size(1920, 1080, 1, 9, 16));

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void Screen_is_window_times_scale_unchanged(int scale) =>
        Assert.Equal(new CaptureSize(1920 * scale, 1080 * scale), CaptureSizing.OutputSize(1920, 1080, scale, null));

    [Fact]
    public void Screen_keeps_the_scale_halving_caps() =>
        // 4K at 4× = 133 MP → 2× (the pre-shape validator behaviour, pinned in CaptureRequestValidatorTests too).
        Assert.Equal(new CaptureSize(7680, 4320), CaptureSizing.OutputSize(3840, 2160, 4, null));

    [Fact]
    public void Screen_honours_a_smaller_gpu_texture_limit() =>
        Assert.Equal(new CaptureSize(3840, 2160), CaptureSizing.OutputSize(1920, 1080, 4, null, maxTextureSize: 4096));

    [Fact]
    public void Gpu_texture_limit_scales_both_sides_down_equally()
    {
        Assert.Equal(new CaptureSize(2048, 2048), Size(1920, 1080, 2, 1, 1, maxTex: 2048));
        Assert.Equal(new CaptureSize(1688, 3000), Size(1920, 1080, 2, 9, 16, maxTex: 3000));
    }

    [Fact]
    public void Pixel_budget_scales_both_sides_down_equally_and_keeps_the_shape()
    {
        Assert.Equal(new CaptureSize(8000, 8000), Size(3840, 2160, 4, 1, 1));   // 15360² = 236 MP → exactly 64 MP
        var p = Size(3840, 2160, 4, 9, 16);                                      // 8640 × 15360 = 133 MP
        Assert.Equal(new CaptureSize(6000, 10666), p);
        Assert.True((long)p.Width * p.Height <= CaptureSizing.MaxPixels);
        Assert.InRange((double)p.Width / p.Height, 9d / 16d - 0.001, 9d / 16d + 0.001);
    }

    [Fact]
    public void Every_shape_at_4x_on_8k_fits_both_caps()
    {
        foreach (var (aw, ah) in new[] { (9, 16), (4, 5), (2, 3), (1, 1), (21, 9), (1, 4), (4, 1) })
        {
            var s = Size(7680, 4320, 4, aw, ah);
            Assert.True(Math.Max(s.Width, s.Height) <= CaptureSizing.MaxLongSide, $"{aw}:{ah} {s}");
            Assert.True((long)s.Width * s.Height <= CaptureSizing.MaxPixels, $"{aw}:{ah} {s}");
            Assert.False(s.IsEmpty);
        }
    }

    [Fact]
    public void Short_side_is_even_and_square_stays_square_on_an_odd_long_side()
    {
        Assert.Equal(0, Size(1920, 1080, 1, 21, 9).Height % 2);
        Assert.Equal(new CaptureSize(1365, 1365), Size(1365, 767, 1, 1, 1));
    }

    [Fact]
    public void Portrait_window_uses_its_long_side_too() => Assert.Equal(new CaptureSize(1920, 822), Size(1080, 1920, 1, 21, 9));

    [Theory]
    [InlineData(0, 16)] [InlineData(9, 0)] [InlineData(-9, 16)] [InlineData(1, 5)] [InlineData(5, 1)]
    public void Invalid_shape_gives_an_empty_size(int aw, int ah) => Assert.True(Size(1920, 1080, 2, aw, ah).IsEmpty);

    [Fact]
    public void Empty_window_gives_an_empty_size() => Assert.True(Size(0, 1080, 2, 9, 16).IsEmpty);

    [Theory]
    [InlineData(1, 4, true)] [InlineData(4, 1, true)] [InlineData(9, 16, true)] [InlineData(21, 9, true)]
    [InlineData(1, 5, false)] [InlineData(0, 1, false)] [InlineData(-1, -1, false)]
    public void Aspect_validity_is_positive_and_within_1_to_4(int aw, int ah, bool valid) =>
        Assert.Equal(valid, new CaptureAspect(aw, ah).IsValid);

    // Guide = the largest centred rectangle of the shape: full height when narrower than the window, full width when wider.
    [Fact]
    public void Guide_rect_on_16_9()
    {
        var p = CaptureSizing.GuideRect(1920, 1080, new CaptureAspect(9, 16));
        Assert.Equal(0f, p.Y); Assert.Equal(1f, p.Height);
        Assert.Equal(607.5f / 1920f, p.Width, 5);
        Assert.Equal((1f - p.Width) / 2f, p.X, 5);
        var wide = CaptureSizing.GuideRect(1920, 1080, new CaptureAspect(21, 9));
        Assert.Equal(0f, wide.X); Assert.Equal(1f, wide.Width);
        Assert.Equal(16f / 21f, wide.Height, 5);
        Assert.Equal((1f - wide.Height) / 2f, wide.Y, 5);
        Assert.Equal(new NormalizedRect(0f, 0f, 1f, 1f), CaptureSizing.GuideRect(1920, 1080, null));
    }

    // The guide frames the photo: its on-screen pixel shape is the output's shape (2× on 1920 × 1080), and a wide
    // shot is exactly the guide's pixels × the scale (it spans the window's full width).
    [Theory]
    [InlineData(9, 16)] [InlineData(4, 5)] [InlineData(2, 3)] [InlineData(1, 1)] [InlineData(21, 9)]
    public void Guide_has_the_output_shape(int aw, int ah)
    {
        var g = CaptureSizing.GuideRect(1920, 1080, new CaptureAspect(aw, ah));
        var s = Size(1920, 1080, 2, aw, ah);
        Assert.InRange(g.Width * 1920d / (g.Height * 1080d) - (double)s.Width / s.Height, -0.002, 0.002);
    }

    [Fact]
    public void Wide_guide_times_scale_is_the_output_size()
    {
        var g = CaptureSizing.GuideRect(1920, 1080, new CaptureAspect(21, 9));
        Assert.Equal(new CaptureSize(3840, 1646), new CaptureSize((int)(g.Width * 3840f), 2 * (int)System.Math.Round(g.Height * 2160f / 2f)));
    }

    [Fact]
    public void Narrow_shapes_keep_the_vertical_view_angle() =>
        Assert.Equal(45f, CaptureSizing.VerticalFieldOfView(45f, 16d / 9d, 9d / 16d));

    [Fact]
    public void Wide_shapes_keep_the_horizontal_view_angle()
    {
        var v = CaptureSizing.VerticalFieldOfView(45f, 16d / 9d, 21d / 9d);
        Assert.Equal(35.03f, v, 2);
        static double Horizontal(double vDeg, double aspect) => 2 * Math.Atan(Math.Tan(vDeg * Math.PI / 360) * aspect);
        Assert.Equal(Horizontal(45, 16d / 9d), Horizontal(v, 21d / 9d), 5);
    }
}
