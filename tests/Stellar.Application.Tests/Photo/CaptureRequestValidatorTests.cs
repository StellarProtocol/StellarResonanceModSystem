using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class CaptureRequestValidatorTests
{
    private static CaptureRequest R(int scale) => new() { Scale = scale, Directory = "/tmp/x", FileStem = "BPSR_a" };

    [Theory]
    [InlineData(3)] [InlineData(0)] [InlineData(8)]
    public void Rejects_scale_outside_1_2_4(int scale) => Assert.NotNull(CaptureRequestValidator.Validate(R(scale), 1920, 1080).Error);

    [Fact]
    public void Caps_long_side_at_16384_by_lowering_scale()
    {
        var (scale, err) = CaptureRequestValidator.Validate(R(4), 5120, 1440);
        Assert.Null(err);
        Assert.Equal(2, scale);
    }

    // Photo Studio fw fix round (perf review): 4K at 4× is 15360×8640 = 133 MP (a 531 MB RGBA frame) — under the
    // 16384 long-side cap but far over the 64 MP total-pixel cap, so it drops to 2× (33 MP).
    [Fact]
    public void Caps_total_pixels_at_64_megapixels_by_lowering_scale()
    {
        var (scale, err) = CaptureRequestValidator.Validate(R(4), 3840, 2160);
        Assert.Null(err);
        Assert.Equal(2, scale);
    }

    [Fact]
    public void Pixel_cap_keeps_lowering_until_it_fits()
    {
        // 6000×6000 at 2× = 144 MP > 64 MP → 1× (36 MP); the long side (12000) alone would have allowed 2×.
        Assert.Equal(1, CaptureRequestValidator.Validate(R(2), 6000, 6000).Scale);
    }

    [Fact]
    public void Exactly_at_the_pixel_cap_is_allowed() =>
        Assert.Equal(2, CaptureRequestValidator.Validate(R(2), 4000, 4000).Scale);   // 8000×8000 = 64,000,000

    [Fact]
    public void Keeps_scale_when_within_cap() => Assert.Equal(4, CaptureRequestValidator.Validate(R(4), 2560, 1440).Scale);

    [Theory]
    [InlineData("")] [InlineData("a/b")] [InlineData("a:b")]
    public void Rejects_bad_stem(string stem) =>
        Assert.NotNull(CaptureRequestValidator.Validate(R(1) with { FileStem = stem }, 1920, 1080).Error);

    [Fact]
    public void Rejects_empty_directory() =>
        Assert.NotNull(CaptureRequestValidator.Validate(R(1) with { Directory = "" }, 1920, 1080).Error);

    [Theory]
    [InlineData("CON")] [InlineData("con")] [InlineData("PRN")] [InlineData("Aux")] [InlineData("nul")]
    [InlineData("COM1")] [InlineData("com9")] [InlineData("LPT1")] [InlineData("lpt9")]
    [InlineData("CON.png")] [InlineData("con.txt.bak")]
    public void Rejects_windows_reserved_stems(string stem) =>
        Assert.NotNull(CaptureRequestValidator.Validate(R(1) with { FileStem = stem }, 1920, 1080).Error);

    [Theory]
    [InlineData("CONcert")] [InlineData("NULLable")] [InlineData("PRNScreen")] [InlineData("LPT10")]
    public void Accepts_stems_that_merely_start_with_a_reserved_word(string stem) =>
        Assert.Null(CaptureRequestValidator.Validate(R(1) with { FileStem = stem }, 1920, 1080).Error);

    [Theory]
    [InlineData("BPSR_a.")] [InlineData("BPSR_a ")]
    public void Rejects_stems_ending_in_dot_or_space(string stem) =>
        Assert.NotNull(CaptureRequestValidator.Validate(R(1) with { FileStem = stem }, 1920, 1080).Error);

    [Theory]
    [InlineData(0)] [InlineData(101)] [InlineData(-1)]
    public void Rejects_jpg_quality_outside_1_to_100(int quality) =>
        Assert.NotNull(CaptureRequestValidator.Validate(R(1) with { Format = CaptureFormat.Jpg, JpgQuality = quality }, 1920, 1080).Error);

    // Tasks 1-5 review carry-over (d): JpgQuality is documented as "ignored for PNG", so a PNG request with an
    // out-of-range quality must not be rejected.
    [Theory]
    [InlineData(0)] [InlineData(101)] [InlineData(-1)]
    public void Png_ignores_jpg_quality(int quality) =>
        Assert.Null(CaptureRequestValidator.Validate(R(1) with { Format = CaptureFormat.Png, JpgQuality = quality }, 1920, 1080).Error);

    [Theory]
    [InlineData(1)] [InlineData(100)] [InlineData(92)]
    public void Accepts_jpg_quality_within_1_to_100(int quality) =>
        Assert.Null(CaptureRequestValidator.Validate(R(1) with { Format = CaptureFormat.Jpg, JpgQuality = quality }, 1920, 1080).Error);

    // Photo shapes spec § Framework: a shape is positive and within 1:4 … 4:1.
    [Theory]
    [InlineData(0, 16)] [InlineData(9, 0)] [InlineData(-9, 16)] [InlineData(1, 5)] [InlineData(5, 1)]
    public void Rejects_invalid_shapes(int aw, int ah) =>
        Assert.NotNull(CaptureRequestValidator.Validate(R(2) with { Aspect = new CaptureAspect(aw, ah) }, 1920, 1080).Error);

    [Theory]
    [InlineData(1, 4)] [InlineData(4, 1)] [InlineData(9, 16)] [InlineData(4, 5)] [InlineData(2, 3)] [InlineData(1, 1)] [InlineData(21, 9)]
    public void Accepts_valid_shapes(int aw, int ah) =>
        Assert.Null(CaptureRequestValidator.Validate(R(2) with { Aspect = new CaptureAspect(aw, ah) }, 1920, 1080).Error);

    [Fact]
    public void Gpu_texture_limit_lowers_the_window_shaped_scale() =>
        Assert.Equal(2, CaptureRequestValidator.Validate(R(4), 1920, 1080, maxTextureSize: 4096).Scale);
}
