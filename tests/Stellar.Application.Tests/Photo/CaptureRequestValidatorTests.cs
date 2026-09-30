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
}
