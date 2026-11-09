using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Theme;
using Stellar.Infrastructure.UI.Style;
using Xunit;

namespace Stellar.Application.Tests.UI;

// Pure-math pins for the framework house-style kit (no Unity): the R1 radius, the window-opacity fade, the R7 label
// rules on the presets that motivated them, the status colours now coming from the theme, and the feedback line timer.
public sealed class StellarStyleTokenTests
{
    // IThemeColors straight from a built-in preset table (what PresetColorsView returns with no overrides).
    private sealed class PresetColors : IThemeColors
    {
        private readonly ColorRgba[] _t;
        public PresetColors(ThemePreset p) => _t = ThemePresets.Tables[p];
        public ColorRgba Accent => _t[ThemePresets.Accent];
        public ColorRgba Gold => _t[ThemePresets.Gold];
        public ColorRgba HpFill => _t[ThemePresets.HpFill];
        public ColorRgba MpFill => _t[ThemePresets.MpFill];
        public ColorRgba Stamina => _t[ThemePresets.Stamina];
        public ColorRgba TextPrimary => _t[ThemePresets.TextPrimary];
        public ColorRgba TextMuted => _t[ThemePresets.TextMuted];
        public ColorRgba Warning => _t[ThemePresets.Warning];
        public ColorRgba HudText => _t[ThemePresets.HudText];
        public ColorRgba HudTextShadow => _t[ThemePresets.HudTextShadow];
        public ColorRgba HudAccent => _t[ThemePresets.HudAccent];
        public ColorRgba HudBarBg => _t[ThemePresets.HudBarBg];
        public ColorRgba HudPillBg => _t[ThemePresets.HudPillBg];
        public ColorRgba MenuBackground => _t[ThemePresets.MenuBackground];
        public ColorRgba MenuText => _t[ThemePresets.MenuText];
        public ColorRgba MenuMuted => _t[ThemePresets.MenuMuted];
        public ColorRgba MenuAccent => _t[ThemePresets.MenuAccent];
        public ColorRgba MenuBorder => _t[ThemePresets.MenuBorder];
        public ColorRgba Success => _t[ThemePresets.Success];
        public ColorRgba Danger => _t[ThemePresets.Danger];
        public ColorRgba Caution => _t[ThemePresets.Caution];
    }

    [Theory]
    [InlineData(28f, 13f)]
    [InlineData(24f, 11f)]
    public void RoundRadius_IsHalfTheSideMinusOne(float side, float radius)
        => Assert.Equal(radius, StellarStyle.RoundRadius(side));

    [Fact]
    public void Fill_MultipliesAlphaByTheLiveWindowOpacity()
    {
        var opacity = 0.5f;
        var s = new StellarStyle(new PresetColors(ThemePreset.Default), () => opacity);
        Assert.Equal(0.5f, s.Fill(s.Panel()).A, 3);
        opacity = 1f;   // live: no rebuild needed
        Assert.Equal(1f, s.Fill(s.Panel()).A, 3);
        Assert.Equal(1f, new StellarStyle(new PresetColors(ThemePreset.Default), (IChromeStyle?)null).Opacity);
    }

    [Fact]
    public void Sakura_PrimaryAndDangerLabels_StayWhite()
    {
        // R7 / option B: the #C8508F accent keeps a white label (≈4.2:1 ≥ 3.0) instead of flipping dark.
        var s = new StellarStyle(new PresetColors(ThemePreset.Sakura), (IChromeStyle?)null);
        Assert.Equal(StellarStyle.White, s.PrimaryLabel());
        Assert.Equal(StellarStyle.White, s.DangerLabel());
    }

    [Fact]
    public void Default_PrimaryLabel_FlipsToARealDarkLabel()
    {
        // Teal #5FB8C4: white is only 2.3:1, so the label flips dark — and must reach 4.5:1, not stop at a mid grey
        // just past 3:1 (the washed-out #535353 seen in game, 2026-11-09).
        var s = new StellarStyle(new PresetColors(ThemePreset.Default), (IChromeStyle?)null);
        var label = s.PrimaryLabel();
        Assert.NotEqual(StellarStyle.White, label);
        Assert.True(StellarStyle.ContrastRatio(StellarStyle.Luminance(label), StellarStyle.Luminance(s.Primary())) >= 4.5f);
    }

    [Fact]
    public void Disabled_HasNoFill_AndAFadedMutedLabel()
    {
        var s = new StellarStyle(new PresetColors(ThemePreset.Default), (IChromeStyle?)null);
        Assert.Equal(0f, StellarStyle.DisabledFill().A);
        Assert.True(s.DisabledLabel().A < 1f);
        Assert.Equal(1f, s.DisabledOutline().A, 3);
    }

    [Fact]
    public void StatusColours_ComeFromTheTheme()
    {
        var light = new StellarStyle(new PresetColors(ThemePreset.Light), (IChromeStyle?)null);
        Assert.Equal(ThemePresets.Tables[ThemePreset.Light][ThemePresets.Success], light.Success());
        Assert.Equal(ThemePresets.Tables[ThemePreset.Light][ThemePresets.Caution], light.Caution());
    }

    [Fact]
    public void FeedbackLine_ShowsForThreeSeconds()
    {
        var now = 100f;
        var s = new StellarStyle(new PresetColors(ThemePreset.Default), (IChromeStyle?)null);
        var line = new StellarFeedbackLine(s, () => "tip", () => now);
        Assert.False(line.IsShowing);
        line.Show("Raid Manager turned off");
        Assert.True(line.IsShowing);
        now += StellarFeedbackLine.ShowSeconds - 0.1f;
        Assert.True(line.IsShowing);
        now += 0.2f;
        Assert.False(line.IsShowing);
    }
}
