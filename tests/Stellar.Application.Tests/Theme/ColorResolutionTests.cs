// tests/Stellar.Application.Tests/Theme/ColorResolutionTests.cs
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

public sealed class ColorResolutionTests
{
    private static IReadOnlyDictionary<ThemePreset, ColorRgba> Defaults() =>
        new Dictionary<ThemePreset, ColorRgba>
        {
            [ThemePreset.Default] = new(0.1f,0,0), [ThemePreset.Dark] = new(0,0.1f,0),
            [ThemePreset.Light]  = new(0,0,0.1f), [ThemePreset.Crimson] = new(0.1f,0,0.1f),
        };

    [Fact]
    public void Override_WinsOverDefault_OnActiveCustomTheme()
    {
        var theme = new FakeNamedTheme(ThemePreset.Crimson);
        theme.SetActiveCustom("Sakura", ThemePreset.Crimson);
        var store = new MemoryOverrideStore();
        var svc = new ColorRegistryService(theme, store);
        svc.Register("k", "k", Defaults());
        ((IThemeOverrides)svc).SetOverride("k", new(0.9f, 0.2f, 0.5f));
        Assert.Equal(new ColorRgba(0.9f,0.2f,0.5f), svc.Resolve("k"));
    }

    [Fact]
    public void CustomTheme_WithNoOverride_FallsBackToBasePresetDefault()
    {
        var theme = new FakeNamedTheme(ThemePreset.Crimson);
        theme.SetActiveCustom("Sakura", ThemePreset.Crimson);
        var svc = new ColorRegistryService(theme, new MemoryOverrideStore());
        svc.Register("k", "k", Defaults());
        Assert.Equal(new ColorRgba(0.1f,0,0.1f), svc.Resolve("k"));
    }

    [Fact]
    public void BuiltInActive_IgnoresOverrides()
    {
        var theme = new FakeNamedTheme(ThemePreset.Dark);
        var store = new MemoryOverrideStore();
        store.Set("Sakura", "k", new(0.9f,0.9f,0.9f));
        var svc = new ColorRegistryService(theme, store);
        svc.Register("k", "k", Defaults());
        Assert.Equal(new ColorRgba(0,0.1f,0), svc.Resolve("k"));
    }

    [Fact]
    public void ClearOverride_RevertsToDefault()
    {
        var theme = new FakeNamedTheme(ThemePreset.Dark);
        theme.SetActiveCustom("S", ThemePreset.Dark);
        var svc = new ColorRegistryService(theme, new MemoryOverrideStore());
        svc.Register("k", "k", Defaults());
        var ov = (IThemeOverrides)svc;
        ov.SetOverride("k", new(1,1,1));
        ov.ClearOverride("k");
        Assert.False(ov.HasOverride("k"));
        Assert.Equal(new ColorRgba(0,0.1f,0), svc.Resolve("k"));
    }

    [Fact]
    public void Slots_ListsRegistered_GroupedDataByOwner()
    {
        var svc = new ColorRegistryService(new FakeNamedTheme(ThemePreset.Dark), new MemoryOverrideStore());
        svc.Register("PlayerHUD.Stamina", "Stamina", Defaults());
        svc.Register("CombatMeter.Mage", "Mage", Defaults());
        var slots = ((IThemeOverrides)svc).Slots;
        Assert.Equal(2, slots.Count);
        Assert.Contains(slots, s => s.Owner == "PlayerHUD" && s.Label == "Stamina");
        Assert.Contains(slots, s => s.Owner == "CombatMeter");
    }
}
