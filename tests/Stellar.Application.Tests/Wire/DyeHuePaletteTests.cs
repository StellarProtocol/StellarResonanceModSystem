using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// <see cref="DyeHuePalette"/> mirrors <c>fashion_data.lua</c> <c>convertProtoVectorToHSV</c> (release_3.7):
/// Board (Type 3) → x is degrees; any other type → x is matched against the FIRST element of each
/// <c>Hue</c> row (<c>[id, degrees]</c>), miss or x == 0 → 0. Rows below are real ColorGroupTable data.
/// </summary>
public sealed class DyeHuePaletteTests
{
    // ColorGroupTable id 2 (Type 1 = Option).
    private static readonly int[][] Group2Hue =
    {
        new[] { 1, 31 }, new[] { 2, 32 }, new[] { 3, 26 }, new[] { 4, 25 },
        new[] { 5, 17 }, new[] { 6, 18 }, new[] { 7, 10 }, new[] { 8, 15 },
    };

    [Fact]
    public void Board_passes_degrees_through()
    {
        var p = DyeHuePalette.FromGroup(3, Group2Hue);   // Hue column ignored for Board
        Assert.Same(DyeHuePalette.Board, p);
        Assert.Equal(200, p.ToDegrees(200));
        Assert.Equal(0, p.ToDegrees(0));
    }

    [Theory]
    [InlineData(1, 31)]   // ids are 1-based: swatch 1 → first row
    [InlineData(3, 26)]
    [InlineData(8, 15)]   // last row
    [InlineData(0, 0)]    // x == 0 → hue 0 (Lua guard)
    [InlineData(9, 0)]    // no matching id → hue 0
    public void Option_maps_swatch_id_to_degrees(int x, int expected)
        => Assert.Equal(expected, DyeHuePalette.FromGroup(1, Group2Hue).ToDegrees(x));

    [Fact]
    public void Option_matches_by_id_not_position()
    {
        // Ids out of positional order: x selects the row whose id equals x.
        var p = DyeHuePalette.FromGroup(1, new[] { new[] { 5, 100 }, new[] { 1, 200 } });
        Assert.Equal(200, p.ToDegrees(1));
        Assert.Equal(100, p.ToDegrees(5));
    }

    [Fact]
    public void Slider_type_uses_the_swatch_rule_like_the_client()
    {
        // The Lua only special-cases Board; Slider (2) falls into the same id lookup.
        Assert.Equal(32, DyeHuePalette.FromGroup(2, Group2Hue).ToDegrees(2));
    }

    [Fact]
    public void Short_rows_are_skipped_and_no_group_is_always_zero()
    {
        var p = DyeHuePalette.FromGroup(1, new[] { new[] { 1 }, new[] { 2, 90 } });
        Assert.Equal(0, p.ToDegrees(1));
        Assert.Equal(90, p.ToDegrees(2));
        Assert.Equal(0, DyeHuePalette.NoGroup.ToDegrees(200));
    }
}
