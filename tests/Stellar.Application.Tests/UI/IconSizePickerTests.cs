using Stellar.Infrastructure.UI;
using Stellar.Infrastructure.UI.Style;
using Xunit;

namespace Stellar.Application.Tests.UI;

// Icon art rule (2026-11-09): framework glyphs are pre-drawn per size and drawn at the bitmap whose size equals the
// on-screen px (glyph units × canvas scaleFactor); otherwise the smallest larger bitmap, mipmapped. One class so the
// static LauncherIcons.ScaleSource is never raced by another test.
public sealed class IconSizePickerTests
{
    private static readonly int[] All = { 12, 16, 24, 32, 64, 128 };

    [Theory]
    [InlineData(12f, 12)]
    [InlineData(16f, 16)]
    [InlineData(24f, 24)]
    [InlineData(32f, 32)]
    [InlineData(128f, 128)]
    public void ExactSize_PicksThatBitmap_Exact(float px, int expected)
        => Assert.Equal((expected, true), IconSizePicker.Pick(px, All));

    // 1920×1080 = scaleFactor 0.75: 16 → 12 and 32 → 24 land on pre-drawn art; 12 → 9 and 24 → 18 take the next one up.
    [Theory]
    [InlineData(16f, 12, true)]
    [InlineData(32f, 24, true)]
    [InlineData(12f, 12, false)]
    [InlineData(24f, 24, false)]
    public void Scale075_ExactOrNearestLarger(float glyph, int expected, bool exact)
        => Assert.Equal((expected, exact), IconSizePicker.Pick(IconSizePicker.DisplayPx(glyph, 0.75f), All));

    // UI scale 1.25 at 1440p: 16 → 20 → the 24 art (minified, mips); 32 → 40 → 64.
    [Theory]
    [InlineData(16f, 24)]
    [InlineData(32f, 64)]
    [InlineData(24f, 32)]
    public void UiScale125_NearestLarger_NotExact(float glyph, int expected)
        => Assert.Equal((expected, false), IconSizePicker.Pick(IconSizePicker.DisplayPx(glyph, 1.25f), All));

    [Fact]
    public void FloatNoise_StillExact() => Assert.Equal((12, true), IconSizePicker.Pick(12.00001f, All));

    [Fact]
    public void MissingSize_TakesNextLarger()
        => Assert.Equal((32, false), IconSizePicker.Pick(16f, new[] { 12, 32, 128 }));

    [Fact]
    public void AboveLargest_TakesLargest() => Assert.Equal((128, false), IconSizePicker.Pick(200f, All));

    [Fact]
    public void NoSizes_ReturnsNone() => Assert.Equal((-1, false), IconSizePicker.Pick(16f, System.Array.Empty<int>()));

    [Fact]
    public void NonPositiveScale_IsUnscaled() => Assert.Equal(16f, IconSizePicker.DisplayPx(16f, 0f));

    [Theory]
    [InlineData(40f, 24)]
    [InlineData(32f, 16)]
    [InlineData(28f, 16)]
    public void IconTileGlyph_SnapsDownToPreDrawn(float tile, int expected)
        => Assert.Equal(expected, StellarStyle.IconTileGlyph(tile));

    // ── LauncherIcons over the real embedded set ─────────────────────────────────────────────────────────────────
    [Fact]
    public void Embedded_ExactPick_IsPixelExact_FallbackIsSeparateArray()
    {
        var exact = LauncherIcons.Get("discord", 16);
        var scaled = LauncherIcons.Get("discord", 14);   // no 14 art → the 16 art, mipmapped
        Assert.NotNull(exact);
        Assert.NotNull(scaled);
        Assert.True(LauncherIcons.IsPixelExact(exact));
        Assert.False(LauncherIcons.IsPixelExact(scaled));
        Assert.NotSame(exact, scaled);
        Assert.Equal(exact, scaled);                      // same pixels, different texture key
        Assert.Same(exact, LauncherIcons.Get("discord", 16));   // stable reference for the Live image diff
    }

    [Fact]
    public void Embedded_UnsizedGet_Is128Art_NotPixelExact()
    {
        var master = LauncherIcons.Get("settings");
        Assert.NotNull(master);
        Assert.False(LauncherIcons.IsPixelExact(master));
        Assert.Equal(LauncherIcons.Get("settings", 128), master);
    }

    [Fact]
    public void Embedded_BrandHasNoSizes_FallsBackToFlat()
        => Assert.Same(LauncherIcons.Get("stellar"), LauncherIcons.Get("stellar", 16));

    [Fact]
    public void Glyph_FollowsScaleSource()
    {
        try
        {
            LauncherIcons.ScaleSource = () => 0.75f;
            Assert.Same(LauncherIcons.Get("star", 12), LauncherIcons.Glyph("star", 16f));
            LauncherIcons.ScaleSource = () => 1f;
            Assert.Same(LauncherIcons.Get("star", 16), LauncherIcons.Glyph("star", 16f));
        }
        finally { LauncherIcons.ScaleSource = null; }
    }
}
