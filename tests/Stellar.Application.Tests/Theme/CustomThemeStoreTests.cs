using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

public sealed class CustomThemeStoreTests
{
    [Fact]
    public void Create_AddsNamedTheme_WithBase()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        s.Create("Blossom", ThemePreset.Crimson);
        Assert.Contains("Blossom", s.Names);
        Assert.Equal(ThemePreset.Crimson, s.BasePresetOf("Blossom"));
    }

    [Fact]
    public void Create_DuplicateName_Throws()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        s.Create("Blossom", ThemePreset.Crimson);
        Assert.Throws<System.ArgumentException>(() => s.Create("Blossom", ThemePreset.Dark));
    }

    [Fact]
    public void Rename_ChangesName_KeepsBase()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        s.Create("Blossom", ThemePreset.Crimson);
        s.Rename("Blossom", "Bloom");
        Assert.DoesNotContain("Blossom", s.Names);
        Assert.Equal(ThemePreset.Crimson, s.BasePresetOf("Bloom"));
    }

    [Fact]
    public void Delete_Removes()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        s.Create("Blossom", ThemePreset.Crimson);
        s.Delete("Blossom");
        Assert.Empty(s.Names);
    }

    [Fact]
    public void Themes_PersistAcrossReload()
    {
        var cfg = new InMemoryConfigSection();
        new CustomThemeStore(cfg).Create("Blossom", ThemePreset.Crimson);
        var reloaded = new CustomThemeStore(cfg);
        Assert.Equal(ThemePreset.Crimson, reloaded.BasePresetOf("Blossom"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    public void Create_InvalidName_Throws(string bad)
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        Assert.Throws<System.ArgumentException>(() => s.Create(bad, ThemePreset.Dark));
    }

    [Fact]
    public void Create_TrimsSurroundingWhitespace()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        s.Create("  Blossom  ", ThemePreset.Crimson);
        Assert.Contains("Blossom", s.Names);
    }

    // 2.30.0: a theme whose saved base preset this build can't parse (a downgrade reading "Sakura", or any future
    // preset) is KEPT, based on Default — before, it was skipped at load and erased by the next save.
    [Fact]
    public void UnknownBasePreset_IsKept_BasedOnDefault()
    {
        var cfg = new InMemoryConfigSection();
        cfg.Set("customThemes", new Dictionary<string, string> { ["Rose"] = "Nebula", ["Mint"] = "Dark", ["Odd"] = "7" });
        var s = new CustomThemeStore(cfg);
        Assert.Equal(new[] { "Rose", "Mint", "Odd" }, s.Names);
        Assert.Equal(ThemePreset.Default, s.BasePresetOf("Rose"));
        Assert.Equal(ThemePreset.Dark, s.BasePresetOf("Mint"));
        Assert.Equal(ThemePreset.Default, s.BasePresetOf("Odd"));   // an undefined numeric value is unknown too
    }

    [Fact]
    public void UnknownBasePreset_SurvivesASave_WithItsOriginalBaseString()
    {
        var cfg = new InMemoryConfigSection();
        cfg.Set("customThemes", new Dictionary<string, string> { ["Rose"] = "Nebula" });
        var s = new CustomThemeStore(cfg);
        s.Create("Mint", ThemePreset.Dark);    // any write persists the whole map
        s.Rename("Rose", "Bloom");             // the raw base follows a rename
        var raw = cfg.Get<Dictionary<string, string>>("customThemes", null)!;
        Assert.Equal("Nebula", raw["Bloom"]);
        Assert.Equal("Dark", raw["Mint"]);
        var reloaded = new CustomThemeStore(cfg);
        Assert.Contains("Bloom", reloaded.Names);
    }

    [Fact]
    public void Create_RefusesBeyondTheLimit()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        for (var i = 0; i < CustomThemeStore.MaxCustomThemes; i++) s.Create("T" + i, ThemePreset.Default);
        Assert.False(s.CanCreate);
        Assert.Throws<System.ArgumentException>(() => s.Create("OneTooMany", ThemePreset.Default));
        Assert.Equal(CustomThemeStore.MaxCustomThemes, s.Names.Count);
        s.Delete("T0");
        Assert.True(s.CanCreate);
    }

    [Fact]
    public void ExistingConfigOverTheLimit_LoadsInFull_ButCreatesNothingMore()
    {
        var cfg = new InMemoryConfigSection();
        var raw = new Dictionary<string, string>();
        for (var i = 0; i < CustomThemeStore.MaxCustomThemes + 3; i++) raw["T" + i] = "Default";
        cfg.Set("customThemes", raw);
        var s = new CustomThemeStore(cfg);
        Assert.Equal(CustomThemeStore.MaxCustomThemes + 3, s.Names.Count);   // never drops user data
        Assert.False(s.CanCreate);
        Assert.Throws<System.ArgumentException>(() => s.Create("New", ThemePreset.Default));
        s.Rename("T0", "Renamed");                                            // existing themes stay editable
        Assert.Contains("Renamed", s.Names);
    }

    [Fact]
    public void Rename_SameName_IsNoOp()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        s.Create("Blossom", ThemePreset.Crimson);
        s.Rename("Blossom", "Blossom"); // must not throw
        Assert.Contains("Blossom", s.Names);
        Assert.Equal(ThemePreset.Crimson, s.BasePresetOf("Blossom"));
    }

    // 2.30.0: a custom theme named like a built-in ("Default" next to the built-in Default) is confusing — refused for
    // new and renamed themes, any case; an existing one still loads (user data is never dropped).
    [Theory]
    [InlineData("Default")]
    [InlineData("sakura")]
    [InlineData(" LIGHT ")]
    public void BuiltInName_IsRefused_ForCreateAndRename(string name)
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        Assert.Throws<System.ArgumentException>(() => s.Create(name, ThemePreset.Dark));
        s.Create("Mine", ThemePreset.Dark);
        Assert.Throws<System.ArgumentException>(() => s.Rename("Mine", name));
        Assert.Contains("Mine", s.Names);
    }

    [Fact]
    public void ExistingThemeWithBuiltInName_StillLoads()
    {
        var cfg = new InMemoryConfigSection();
        cfg.Set("customThemes", new Dictionary<string, string> { ["Default"] = "Dark" });
        var s = new CustomThemeStore(cfg);
        Assert.Contains("Default", s.Names);
        Assert.Equal(ThemePreset.Dark, s.BasePresetOf("Default"));
    }
}
