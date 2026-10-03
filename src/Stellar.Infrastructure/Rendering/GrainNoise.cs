using System;
namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// The film-grain texture the framework owns (owner bug 2026-10-01 "film grain shows no change": the game's bundled
/// Thin1 grain texture is most likely flat — recon-party-grain.md). Zero-mean noise around 0.5, approximately
/// Gaussian (sum of three uniforms), the SAME value in R, G, B and A — URP-style shaders read alpha, others read red.
/// Fixed seed, so every session renders the same grain. Pure — no Unity types — so it is unit-tested.
/// </summary>
internal static class GrainNoise
{
    public const int Size = 256;
    private const int Seed = 0x5EED;
    private const double Spread = 0.18;   // ≈ 1 σ of grain around mid-grey

    /// <summary>RGBA32 pixel data, <paramref name="size"/>² × 4 bytes, row-major.</summary>
    public static byte[] Generate(int size = Size, int seed = Seed)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var rng = new Random(seed);
        var data = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            var sum = rng.NextDouble() + rng.NextDouble() + rng.NextDouble();   // mean 1.5, σ 0.5
            var v = Math.Clamp(0.5 + Spread * (sum - 1.5) / 0.5, 0.0, 1.0);
            var b = (byte)Math.Round(v * 255.0);
            data[i * 4] = b;
            data[i * 4 + 1] = b;
            data[i * 4 + 2] = b;
            data[i * 4 + 3] = b;
        }
        return data;
    }
}
