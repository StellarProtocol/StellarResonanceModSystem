// tests/Stellar.Application.Tests/Theme/StatusColorSlotTests.cs
using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.Theme;
using Xunit;

namespace Stellar.Application.Tests.Theme;

// 2.30.0: menu Text / Muted and the status trio (Success / Danger / Caution) became editable framework slots.
// Pins (1) every built-in preset gets a registered default for each (a preset missing from FrameworkColorRegistration's
// list would silently resolve to Default's value), (2) the dark presets keep EXACTLY the colours the house style
// hard-coded before (zero visual change for plugins that adopt them), (3) Light's darkened trio, and (4) a custom
// theme's override is what a read returns.
public sealed class StatusColorSlotTests
{
    private static ColorRgba Hex(uint rgb) =>
        new(((rgb >> 16) & 0xff) / 255f, ((rgb >> 8) & 0xff) / 255f, (rgb & 0xff) / 255f, 1f);

    private static ColorRegistryService Registry(FakeNamedTheme theme, IColorOverrideStore? store = null)
    {
        var svc = new ColorRegistryService(theme, store ?? new NullOverrideStore());
        FrameworkColorRegistration.RegisterAll(svc);
        return svc;
    }

    [Theory]
    [InlineData(ThemePreset.Default)]
    [InlineData(ThemePreset.Dark)]
    [InlineData(ThemePreset.Crimson)]
    [InlineData(ThemePreset.Sakura)]
    public void DarkPresets_KeepTheHouseStyleStatusColours(ThemePreset preset)
    {
        var svc = Registry(new FakeNamedTheme(preset));
        Assert.Equal(Hex(0x5FD38D), svc.Resolve("Theme.Success"));
        Assert.Equal(Hex(0xFF4D4F), svc.Resolve("Theme.Danger"));
        Assert.Equal(Hex(0xFFCC73), svc.Resolve("Theme.Caution"));
    }

    [Fact]
    public void LightPreset_UsesTheDarkenedStatusColours()
    {
        var svc = Registry(new FakeNamedTheme(ThemePreset.Light));
        Assert.Equal(Hex(0x1E8A4C), svc.Resolve("Theme.Success"));
        Assert.Equal(Hex(0xD13438), svc.Resolve("Theme.Danger"));
        Assert.Equal(Hex(0xA86A00), svc.Resolve("Theme.Caution"));
    }

    [Fact]
    public void EveryPreset_HasARegisteredDefault_ForEveryEditableSlot()
    {
        foreach (var preset in (ThemePreset[])Enum.GetValues(typeof(ThemePreset)))
        {
            var svc = Registry(new FakeNamedTheme(preset));
            foreach (var (key, index) in FrameworkColorRegistration.EditableTokens)
                Assert.Equal(ThemePresets.Tables[preset][index], svc.Resolve(key));
        }
    }

    [Fact]
    public void Sakura_IsABuiltInPreset_WithTheApprovedMenuColours()
    {
        var svc = Registry(new FakeNamedTheme(ThemePreset.Sakura));
        Assert.Equal(Hex(0x2E2433), svc.Resolve("Theme.MenuBackground"));
        Assert.Equal(Hex(0xE8DCEF), svc.Resolve("Theme.MenuText"));
        Assert.Equal(Hex(0x9B8AA5), svc.Resolve("Theme.MenuMuted"));
        Assert.Equal(Hex(0xC8508F), svc.Resolve("Theme.MenuAccent"));
        Assert.True(ThemePresets.GlassMenuGradients.ContainsKey(ThemePreset.Sakura));
        Assert.Equal(4, (int)ThemePreset.Sakura);   // appended: existing persisted values never move
    }

    [Fact]
    public void MenuTextAndMuted_ResolveThePresetValues()
    {
        var svc = Registry(new FakeNamedTheme(ThemePreset.Default));
        Assert.Equal(ThemePresets.Tables[ThemePreset.Default][ThemePresets.MenuText], svc.Resolve("Theme.MenuText"));
        Assert.Equal(ThemePresets.Tables[ThemePreset.Default][ThemePresets.MenuMuted], svc.Resolve("Theme.MenuMuted"));
    }

    [Theory]
    [InlineData("Theme.MenuText")]
    [InlineData("Theme.MenuMuted")]
    [InlineData("Theme.Success")]
    [InlineData("Theme.Danger")]
    [InlineData("Theme.Caution")]
    public void CustomThemeOverride_WinsForTheNewSlots(string key)
    {
        var theme = new FakeNamedTheme(ThemePreset.Default);
        theme.SetActiveCustom("Rose", ThemePreset.Default);
        var svc = Registry(theme, new MemoryOverrideStore());
        var pick = new ColorRgba(0.2f, 0.4f, 0.6f, 1f);
        ((IThemeOverrides)svc).SetOverride(key, pick);
        Assert.Equal(pick, svc.Resolve(key));
    }
}
