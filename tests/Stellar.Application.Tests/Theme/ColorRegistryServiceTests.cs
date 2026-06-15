// tests/Stellar.Application.Tests/Theme/ColorRegistryServiceTests.cs
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

public sealed class ColorRegistryServiceTests
{
    private static IReadOnlyDictionary<ThemePreset, ColorRgba> Defaults(
        ColorRgba def, ColorRgba dark, ColorRgba light, ColorRgba crim) =>
        new Dictionary<ThemePreset, ColorRgba>
        {
            [ThemePreset.Default] = def, [ThemePreset.Dark] = dark,
            [ThemePreset.Light]  = light, [ThemePreset.Crimson] = crim,
        };

    private static (ColorRegistryService svc, FakeNamedTheme theme) New()
    {
        var theme = new FakeNamedTheme(ThemePreset.Dark);
        var svc = new ColorRegistryService(theme, new NullOverrideStore());
        return (svc, theme);
    }

    [Fact]
    public void Register_ReturnsSlot_ResolvingActivePresetDefault()
    {
        var (svc, _) = New();
        var slot = svc.Register("PlayerHUD.Stamina", "Stamina bar",
            Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0)));
        Assert.Equal(new ColorRgba(0,1,0), slot.Value); // active = Dark
    }

    [Fact]
    public void Slot_TracksActiveThemeChange()
    {
        var (svc, theme) = New();
        var slot = svc.Register("k", "k",
            Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0)));
        theme.SetActive(ThemePreset.Light);
        Assert.Equal(new ColorRgba(0,0,1), slot.Value);
    }

    [Fact]
    public void Resolve_UnknownKey_ReturnsMagentaSentinel()
    {
        var (svc, _) = New();
        Assert.Equal(ColorRegistryService.MissingSentinel, svc.Resolve("nope"));
    }

    [Fact]
    public void Register_DuplicateKey_Throws()
    {
        var (svc, _) = New();
        var d = Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0));
        svc.Register("k", "k", d);
        Assert.Throws<System.ArgumentException>(() => svc.Register("k", "k2", d));
    }

    [Fact]
    public void Unregister_RemovesSlot()
    {
        var (svc, _) = New();
        var d = Defaults(new(1,0,0), new(0,1,0), new(0,0,1), new(1,1,0));
        svc.Register("k", "k", d);
        svc.Unregister("k");
        Assert.Equal(ColorRegistryService.MissingSentinel, svc.Resolve("k"));
    }
}
