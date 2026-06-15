using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

public sealed class NamedThemeCustomTests
{
    private static NamedThemeService New() =>
        new(new InMemoryConfigSection(), new StubLog());

    [Fact]
    public void SetActiveCustom_ReportsName_AndBaseAsActivePreset()
    {
        var t = New();
        t.SetActiveCustom("Sakura", ThemePreset.Crimson);
        Assert.Equal("Sakura", t.ActiveCustomName);
        Assert.Equal(ThemePreset.Crimson, t.Active);
    }

    [Fact]
    public void SetActive_ClearsCustomName()
    {
        var t = New();
        t.SetActiveCustom("Sakura", ThemePreset.Crimson);
        t.SetActive(ThemePreset.Dark);
        Assert.Null(t.ActiveCustomName);
        Assert.Equal(ThemePreset.Dark, t.Active);
    }

    [Fact]
    public void ActiveCustom_PersistsAcrossReload()
    {
        var cfg = new InMemoryConfigSection();
        new NamedThemeService(cfg, new StubLog()).SetActiveCustom("Sakura", ThemePreset.Crimson);
        var reloaded = new NamedThemeService(cfg, new StubLog());
        Assert.Equal("Sakura", reloaded.ActiveCustomName);
        Assert.Equal(ThemePreset.Crimson, reloaded.Active);
    }
}
