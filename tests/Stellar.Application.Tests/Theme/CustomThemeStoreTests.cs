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
        s.Create("Sakura", ThemePreset.Crimson);
        Assert.Contains("Sakura", s.Names);
        Assert.Equal(ThemePreset.Crimson, s.BasePresetOf("Sakura"));
    }

    [Fact]
    public void Create_DuplicateName_Throws()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        s.Create("Sakura", ThemePreset.Crimson);
        Assert.Throws<System.ArgumentException>(() => s.Create("Sakura", ThemePreset.Dark));
    }

    [Fact]
    public void Rename_ChangesName_KeepsBase()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        s.Create("Sakura", ThemePreset.Crimson);
        s.Rename("Sakura", "Bloom");
        Assert.DoesNotContain("Sakura", s.Names);
        Assert.Equal(ThemePreset.Crimson, s.BasePresetOf("Bloom"));
    }

    [Fact]
    public void Delete_Removes()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        s.Create("Sakura", ThemePreset.Crimson);
        s.Delete("Sakura");
        Assert.Empty(s.Names);
    }

    [Fact]
    public void Themes_PersistAcrossReload()
    {
        var cfg = new InMemoryConfigSection();
        new CustomThemeStore(cfg).Create("Sakura", ThemePreset.Crimson);
        var reloaded = new CustomThemeStore(cfg);
        Assert.Equal(ThemePreset.Crimson, reloaded.BasePresetOf("Sakura"));
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
        s.Create("  Sakura  ", ThemePreset.Crimson);
        Assert.Contains("Sakura", s.Names);
    }

    [Fact]
    public void Rename_SameName_IsNoOp()
    {
        var s = new CustomThemeStore(new InMemoryConfigSection());
        s.Create("Sakura", ThemePreset.Crimson);
        s.Rename("Sakura", "Sakura"); // must not throw
        Assert.Contains("Sakura", s.Names);
        Assert.Equal(ThemePreset.Crimson, s.BasePresetOf("Sakura"));
    }
}
