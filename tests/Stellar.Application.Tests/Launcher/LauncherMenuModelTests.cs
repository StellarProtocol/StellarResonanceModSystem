using System.Linq;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Application.Tests.Theme;
using Xunit;

namespace Stellar.Application.Tests.Launcher;

public sealed class LauncherMenuModelTests
{
    private double _now;

    private LauncherMenuModel NewModel(out LauncherRegistry reg, out InMemoryConfigSection config)
    {
        config = new InMemoryConfigSection();
        reg = new LauncherRegistry(new LauncherPrefs(config), new StubLog());
        return new LauncherMenuModel(reg, () => _now);
    }

    private static LauncherEntry Entry(string title) => new(title, IconPng: null, IconKey: null, OnOpen: () => { });

    private static string[] Shown(LauncherMenuModel m)
        => Enumerable.Range(0, m.ShownCount).Select(i => m.ShownAt(i)!.Title).ToArray();

    [Fact]
    public void Tab_DefaultsToFavorites_AndPersists()
    {
        var m = NewModel(out _, out var config);
        Assert.Equal(LauncherTab.Favorites, m.Tab);
        m.SelectTab(LauncherTab.All);
        Assert.Equal(LauncherTab.All, new LauncherPrefs(config).Tab);
    }

    [Fact]
    public void Prefs_UnknownTabValue_FallsBackToFavorites()
    {
        var config = new InMemoryConfigSection();
        config.Set("tab", "Bogus");
        Assert.Equal(LauncherTab.Favorites, new LauncherPrefs(config).Tab);
        config.Set("tab", "7");
        Assert.Equal(LauncherTab.Favorites, new LauncherPrefs(config).Tab);
    }

    [Fact]
    public void FavoritesTab_ShowsOnlyPinned_AllTabShowsEverything()
    {
        var m = NewModel(out var reg, out _);
        var a = Entry("A"); var b = Entry("B");
        reg.Register(a); reg.Register(b);
        reg.SetPinned(b, true);

        Assert.Equal(new[] { "B" }, Shown(m));
        Assert.Equal(2, m.AllCount);
        Assert.Equal(1, m.FavoriteCount);
        m.SelectTab(LauncherTab.All);
        Assert.Equal(new[] { "A", "B" }, Shown(m));
    }

    [Fact]
    public void UnstarInFavorites_KeepsGhostUntilTabChanges()
    {
        var m = NewModel(out var reg, out _);
        var a = Entry("A");
        reg.Register(a);
        reg.SetPinned(a, true);

        Assert.False(m.ToggleFavorite(a));
        Assert.Equal(new[] { "A" }, Shown(m));
        Assert.True(m.IsGhost(a));
        Assert.Equal(0, m.FavoriteCount);
        Assert.False(m.FavoritesEmpty);

        m.SelectTab(LauncherTab.Favorites);   // any tab click ends the ghosts
        Assert.Empty(Shown(m));
        Assert.True(m.FavoritesEmpty);
    }

    [Fact]
    public void RestarGhost_MakesItNormalAgain()
    {
        var m = NewModel(out var reg, out _);
        var a = Entry("A");
        reg.Register(a);
        reg.SetPinned(a, true);
        m.ToggleFavorite(a);
        Assert.True(m.ToggleFavorite(a));
        Assert.False(m.IsGhost(a));
        m.SelectTab(LauncherTab.Favorites);
        Assert.Equal(new[] { "A" }, Shown(m));
    }

    [Fact]
    public void UnstarInAllTab_IsNotAGhost()
    {
        var m = NewModel(out var reg, out _);
        var a = Entry("A");
        reg.Register(a);
        reg.SetPinned(a, true);
        m.SelectTab(LauncherTab.All);
        m.ToggleFavorite(a);
        Assert.False(m.IsGhost(a));
        m.SelectTab(LauncherTab.Favorites);
        Assert.Empty(Shown(m));
    }

    [Fact]
    public void Find_OnlyAboveThreshold_CaseInsensitive()
    {
        var m = NewModel(out var reg, out _);
        m.SelectTab(LauncherTab.All);
        for (var i = 0; i < LauncherMenuModel.FindThreshold; i++) reg.Register(Entry("Plugin " + i));
        m.Query = "zzz";
        Assert.False(m.FindVisible);
        Assert.Equal(LauncherMenuModel.FindThreshold, m.ShownCount);   // query ignored at ≤ 16

        reg.Register(Entry("Combat Meter"));
        Assert.True(m.FindVisible);
        Assert.True(m.NoMatch);
        m.Query = "  cOMBAT ";
        Assert.Equal(new[] { "Combat Meter" }, Shown(m));
        Assert.False(m.NoMatch);
    }

    [Fact]
    public void ShouldShow_IsReReadAfterTheRebuildInterval()
    {
        var m = NewModel(out var reg, out _);
        var visible = true;
        reg.Register(Entry("A") with { ShouldShow = () => visible });
        Assert.Equal(1, m.AllCount);

        visible = false;
        Assert.Equal(1, m.AllCount);                    // cached within the interval
        _now += LauncherMenuModel.RebuildSeconds;
        Assert.Equal(0, m.AllCount);
    }

    [Fact]
    public void NonPluginGroupEntries_AreNotTiles()
    {
        var m = NewModel(out var reg, out _);
        reg.Register(Entry("Settings") with { Group = LauncherGroup.Settings });
        reg.Register(Entry("A"));
        Assert.Equal(1, m.AllCount);
    }
}
