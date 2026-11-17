using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

// MeterRowElement.Density (fw 2.33.0): opt-in, Normal untouched; Compact / Mini mask what they don't draw.
public sealed class MeterRowDensityRulesTests
{
    private static MeterRowData Full() => new()
    {
        ShowSpec = true, ShowClassName = true, ShowShare = true, ShowAbilityScore = true, ShowVoiceIcon = true,
        OnVoiceIconClick = () => { }, ShowImagine = true, ShowImagineCooldown = true, SpineWidth = 8f,
        DebuffCellSize = 32f, DebuffColumns = 4, ShowCrest = true, ShowHpBar = true, ShowLeaderFlag = true,
        ShowDebuffs = true, HideSheen = false, ShowRank = true, ShowPrimary = true, ShowSecondary = true,
    };

    [Fact]
    public void Density_defaults_to_normal()
        => Assert.Equal(MeterRowDensity.Normal, new MeterRowElement(() => default(MeterRowData)).Density);

    [Theory]
    [InlineData(MeterRowDensity.Normal, 48f)]
    [InlineData(MeterRowDensity.Compact, 24f)]
    [InlineData(MeterRowDensity.Mini, 17f)]
    public void Row_heights(MeterRowDensity d, float h) => Assert.Equal(h, MeterRowDensityRules.RowHeight(d));

    [Fact]
    public void Normal_is_untouched()
    {
        var d = Full();
        MeterRowDensityRules.Mask(ref d, MeterRowDensity.Normal);
        Assert.True(d.ShowSpec && d.ShowShare && d.ShowImagine && d.ShowVoiceIcon && d.ShowDebuffs);
        Assert.Equal(8f, d.SpineWidth);
        Assert.Equal(32f, d.DebuffCellSize);
        Assert.NotNull(d.OnVoiceIconClick);
    }

    [Fact]
    public void Compact_hides_the_second_line_extras_and_pins_its_geometry()
    {
        var d = Full();
        MeterRowDensityRules.Mask(ref d, MeterRowDensity.Compact);
        Assert.False(d.ShowSpec || d.ShowClassName || d.ShowShare || d.ShowAbilityScore || d.ShowVoiceIcon || d.ShowImagine);
        Assert.Null(d.OnVoiceIconClick);
        Assert.Equal(3f, d.SpineWidth);
        Assert.Equal(16f, d.DebuffCellSize);
        Assert.Equal(3, d.DebuffColumns);
        // Compact still draws these.
        Assert.True(d.ShowCrest && d.ShowHpBar && d.ShowLeaderFlag && d.ShowDebuffs && d.ShowRank && d.ShowPrimary && d.ShowSecondary);
        Assert.False(d.HideSheen);
        Assert.Equal(1, MeterRowDensityRules.EffectRows(MeterRowDensity.Compact));
    }

    [Fact]
    public void Mini_is_bar_only()
    {
        var d = Full();
        MeterRowDensityRules.Mask(ref d, MeterRowDensity.Mini);
        Assert.False(d.ShowCrest || d.ShowHpBar || d.ShowLeaderFlag || d.ShowDebuffs || d.ShowSpec || d.ShowShare);
        Assert.True(d.HideSheen);
        Assert.True(d.ShowRank && d.ShowPrimary && d.ShowSecondary);   // rank · name · total · per-second
    }
}
