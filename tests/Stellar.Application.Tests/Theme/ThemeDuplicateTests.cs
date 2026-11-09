using System;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

// "+ Duplicate" copies the SELECTED theme: a custom theme's copy carries its base preset AND every colour override
// (keyed by theme name — the copy once got only the base, so "Pink_copy" came out plain Default); a built-in preset's
// copy is a fresh custom theme with no overrides. The source is never touched and an orphan under the copy's name
// never leaks in. One InMemoryConfigSection stands in for the framework's single config file.
public sealed class ThemeDuplicateTests
{
    private static readonly ColorRgba Red = new(1f, 0f, 0f, 1f);
    private static readonly ColorRgba Green = new(0f, 1f, 0f, 1f);
    private static readonly ColorRgba Pink = new(1f, 0.5f, 0.75f, 1f);

    private static (InMemoryConfigSection Cfg, ColorOverrideStore Ovr, CustomThemeStore Store) New()
    {
        var cfg = new InMemoryConfigSection();
        var ovr = new ColorOverrideStore(cfg);
        return (cfg, ovr, new CustomThemeStore(cfg, ovr));
    }

    [Fact]
    public void Duplicate_OfACustomTheme_CarriesEveryOverride_InOneWrite_AndSurvivesReload()
    {
        var (cfg, ovr, store) = New();
        store.Create("Pink", ThemePreset.Default);
        ovr.Set("Pink", "Theme.accent", Pink);
        ovr.Set("Pink", "Theme.text", Green);
        ovr.Set("Pink", "Plugin.x.hud", Red);
        ovr.Flush();
        var saves = cfg.SaveCallCount;

        store.Duplicate("Pink", "Pink_copy");

        Assert.Equal(saves + 1, cfg.SaveCallCount);   // theme list + copied overrides land in the same write
        Assert.Equal(ThemePreset.Default, store.BasePresetOf("Pink_copy"));
        Assert.True(ovr.TryGet("Pink_copy", "Theme.accent", out var a) && a.G > 0.49f && a.G < 0.51f);
        Assert.True(ovr.Has("Pink_copy", "Theme.text"));
        Assert.True(ovr.Has("Pink_copy", "Plugin.x.hud"));
        Assert.Equal(new[] { "Pink", "Pink_copy" }, store.Names);

        var reloaded = new ColorOverrideStore(cfg);   // what the next session reads from disk
        Assert.True(reloaded.Has("Pink_copy", "Theme.accent"));
        Assert.True(reloaded.Has("Pink_copy", "Plugin.x.hud"));
        Assert.Contains("Pink_copy", new CustomThemeStore(cfg).Names);
    }

    [Fact]
    public void Duplicate_KeepsTheSourcesBasePreset()
    {
        var (_, _, store) = New();
        store.Create("Night", ThemePreset.Crimson);
        store.Duplicate("Night", "Night_copy");
        Assert.Equal(ThemePreset.Crimson, store.BasePresetOf("Night_copy"));
    }

    [Fact]
    public void Duplicate_LeavesTheSourceUntouched()
    {
        var (_, ovr, store) = New();
        store.Create("Pink", ThemePreset.Dark);
        ovr.Set("Pink", "Theme.accent", Pink);
        ovr.Set("Pink", "Theme.border", Red);

        store.Duplicate("Pink", "Pink_copy");
        ovr.Set("Pink_copy", "Theme.accent", Green);   // editing the copy must not reach the source
        ovr.Clear("Pink_copy", "Theme.border");

        Assert.True(ovr.TryGet("Pink", "Theme.accent", out var a) && a.R > 0.99f && a.G < 0.51f);
        Assert.True(ovr.Has("Pink", "Theme.border"));
        Assert.Equal(ThemePreset.Dark, store.BasePresetOf("Pink"));
    }

    [Fact]
    public void Duplicate_OrphanUnderTheCopysName_DoesNotLeakIn()
    {
        var (_, ovr, store) = New();
        store.Create("Pink", ThemePreset.Default);
        ovr.Set("Pink", "Theme.accent", Pink);
        // Left behind by a deleted theme once called "Pink_copy" (Delete keeps overrides): one slot clashes, one doesn't.
        ovr.Set("Pink_copy", "Theme.accent", Green);
        ovr.Set("Pink_copy", "Theme.border", Green);

        store.Duplicate("Pink", "Pink_copy");

        Assert.True(ovr.TryGet("Pink_copy", "Theme.accent", out var a) && a.R > 0.99f);   // the source's, not the orphan's
        Assert.False(ovr.Has("Pink_copy", "Theme.border"));                              // orphan-only slot dropped
    }

    // The built-in branch of ThemeEditorBody.DuplicateActive: a built-in preset has no overrides of its own, so the copy
    // is Create on that preset — and Create drops any orphan under the name.
    [Fact]
    public void CopyOfABuiltIn_HasNoOverrides()
    {
        var (_, ovr, store) = New();
        ovr.Set("Crimson_copy", "Theme.accent", Red);   // orphan of a deleted theme with the generated name

        store.Create("Crimson_copy", ThemePreset.Crimson);

        Assert.Equal(ThemePreset.Crimson, store.BasePresetOf("Crimson_copy"));
        Assert.False(ovr.Has("Crimson_copy", "Theme.accent"));
    }

    [Fact]
    public void Duplicate_RefusesAnUnknownSource_ATakenName_AndTheLimit()
    {
        var (_, _, store) = New();
        store.Create("A", ThemePreset.Default);
        Assert.Throws<ArgumentException>(() => store.Duplicate("Nope", "Nope_copy"));
        Assert.Throws<ArgumentException>(() => store.Duplicate("A", "A"));
        for (var i = 1; i < CustomThemeStore.MaxCustomThemes; i++) store.Create("T" + i, ThemePreset.Default);
        Assert.Throws<ArgumentException>(() => store.Duplicate("A", "A_copy"));
        Assert.DoesNotContain("A_copy", store.Names);
    }
}
