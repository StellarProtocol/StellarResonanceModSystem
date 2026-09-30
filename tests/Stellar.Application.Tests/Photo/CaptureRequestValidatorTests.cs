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
}
