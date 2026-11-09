using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

// IThemeSwatchResolver.ResolveFor: an INACTIVE custom theme resolves to its own stored overrides over its base
// preset (the Themes card swatch bug: "Pink" on Default drew Default's teal while another theme was active).
public sealed class ThemeSwatchResolverTests
{
    private static IReadOnlyDictionary<ThemePreset, ColorRgba> Defaults() =>
        new Dictionary<ThemePreset, ColorRgba>
        {
            [ThemePreset.Default] = new(0.1f, 0, 0), [ThemePreset.Dark] = new(0, 0.1f, 0),
            [ThemePreset.Light] = new(0, 0, 0.1f),
        };

    private static (ColorRegistryService Svc, MemoryOverrideStore Store) Make(FakeNamedTheme theme)
    {
        var store = new MemoryOverrideStore();
        var svc = new ColorRegistryService(theme, store);
        svc.Register("Theme.MenuAccent", "accent", Defaults());
        return (svc, store);
    }

    [Fact]
    public void InactiveCustomTheme_UsesItsOwnOverride()
    {
        var theme = new FakeNamedTheme(ThemePreset.Dark);   // a built-in is active
        var (svc, store) = Make(theme);
        store.Set("Pink", "Theme.MenuAccent", new(0.77f, 0.37f, 0.61f));
        Assert.Equal(new ColorRgba(0.77f, 0.37f, 0.61f),
            ((IThemeSwatchResolver)svc).ResolveFor("Pink", ThemePreset.Default, "Theme.MenuAccent"));
        Assert.Equal(new ColorRgba(0, 0.1f, 0), svc.Resolve("Theme.MenuAccent"));   // live theme untouched
    }

    [Fact]
    public void InactiveCustomTheme_WithoutOverride_FallsBackToItsBasePreset()
    {
        var theme = new FakeNamedTheme(ThemePreset.Dark);
        theme.SetActiveCustom("Other", ThemePreset.Dark);
        var (svc, store) = Make(theme);
        store.Set("Other", "Theme.MenuAccent", new(0.5f, 0.5f, 0.5f));   // another theme's override must not leak
        Assert.Equal(new ColorRgba(0, 0, 0.1f),
            ((IThemeSwatchResolver)svc).ResolveFor("Pink", ThemePreset.Light, "Theme.MenuAccent"));
    }

    [Fact]
    public void BuiltInPreset_IgnoresOverrides_AndMissingPresetFallsBackToDefault()
    {
        var (svc, store) = Make(new FakeNamedTheme(ThemePreset.Default));
        store.Set("Pink", "Theme.MenuAccent", new(0.9f, 0.9f, 0.9f));
        var r = (IThemeSwatchResolver)svc;
        Assert.Equal(new ColorRgba(0, 0.1f, 0), r.ResolveFor(null, ThemePreset.Dark, "Theme.MenuAccent"));
        Assert.Equal(new ColorRgba(0.1f, 0, 0), r.ResolveFor(null, ThemePreset.Sakura, "Theme.MenuAccent"));
    }
}
