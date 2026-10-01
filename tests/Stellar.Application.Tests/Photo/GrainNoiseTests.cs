using System;
using System.Linq;
using Stellar.Infrastructure.Rendering;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Owner bug 2026-10-01 "film grain shows no visible change" — the framework now feeds the grain pass its own texture.
public sealed class GrainNoiseTests
{
    private static readonly byte[] Pixels = GrainNoise.Generate();

    [Fact]
    public void Is_256_square_rgba32() => Assert.Equal(256 * 256 * 4, Pixels.Length);

    [Fact]
    public void Every_pixel_carries_the_same_value_in_r_g_b_and_a()
    {
        for (var i = 0; i < Pixels.Length; i += 4)
        {
            Assert.Equal(Pixels[i], Pixels[i + 1]);
            Assert.Equal(Pixels[i], Pixels[i + 2]);
            Assert.Equal(Pixels[i], Pixels[i + 3]);
        }
    }

    [Fact]
    public void Mean_is_mid_grey_so_grain_does_not_shift_brightness()
    {
        var mean = Enumerable.Range(0, Pixels.Length / 4).Average(i => Pixels[i * 4 + 3] / 255.0);
        Assert.InRange(mean, 0.49, 0.51);
    }

    [Fact]
    public void Is_not_flat()
    {
        var values = Enumerable.Range(0, Pixels.Length / 4).Select(i => Pixels[i * 4 + 3] / 255.0).ToArray();
        var mean = values.Average();
        var sd = Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        Assert.InRange(sd, 0.12, 0.24);   // ≈ the 0.18 spread
    }

    [Fact]
    public void Is_deterministic() => Assert.Equal(Pixels, GrainNoise.Generate());

    [Fact]
    public void Rejects_a_non_positive_size() => Assert.Throws<ArgumentOutOfRangeException>(() => GrainNoise.Generate(0));
}
