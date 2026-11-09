using System.Linq;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Application.Tests.Theme;
using Xunit;

namespace Stellar.Application.Tests.Launcher;

/// <summary>2.30.0 drag-to-reorder favorites: one saved order shared by the Full menu's Favorites tab and the compact
/// bar; an older config (no order key) keeps registration order; new favorites append; un-favorite removes.</summary>
public sealed class LauncherFavoriteOrderTests
{
    private double _now;

    private LauncherMenuModel NewModel(out LauncherRegistry reg, out InMemoryConfigSection config)
    {
        config = new InMemoryConfigSection();
        reg = new LauncherRegistry(new LauncherPrefs(config), new StubLog());
        return new LauncherMenuModel(reg, () => _now);
    }

    private static LauncherEntry Entry(string title, System.Func<bool>? shows = null)
        => new(title, IconPng: null, IconKey: null, OnOpen: () => { }) { ShouldShow = shows };

    private static string[] Shown(LauncherMenuModel m)
        => Enumerable.Range(0, m.ShownCount).Select(i => m.ShownAt(i)!.Title).ToArray();

    private static string[] Bar(LauncherMenuModel m)
        => Enumerable.Range(0, m.FavoriteCount).Select(i => m.FavoriteAt(i)!.Title).ToArray();

    private static LauncherEntry[] Register(LauncherRegistry reg, params string[] titles)
        => titles.Select(t => { var e = Entry(t); reg.Register(e); return e; }).ToArray();

    // ── pure math ───────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Sort_NoSavedOrder_IsRegistrationOrder() =>
        Assert.Equal(new[] { "A", "B", "C" },
            LauncherFavoriteOrder.Sort(new[] { "A", "B", "C" }, s => s, new string[0]));

    [Fact]
    public void Sort_SavedFirst_UnknownAndUnpinnedSkipped_RestFollowInRegistrationOrder() =>
        Assert.Equal(new[] { "C", "A", "B", "D" },
            LauncherFavoriteOrder.Sort(new[] { "A", "B", "C", "D" }, s => s, new[] { "C", "Gone", "A" }));

    [Fact]
    public void Apply_RefillsOnlyTheSubsetSlots() =>   // H is hidden (not on screen): keeps its slot
        Assert.Equal(new[] { "C", "H", "A" },
            LauncherFavoriteOrder.Apply(new[] { "A", "H", "C" }, new[] { "C", "A" }));

    [Fact]
    public void Apply_SubsetIdNotInFull_IsInsertedWhereTheSubsetPutsIt() =>
        Assert.Equal(new[] { "A", "X", "B" },
            LauncherFavoriteOrder.Apply(new[] { "A", "B" }, new[] { "A", "X", "B" }));

    [Fact]
    public void Move_IsRemoveThenInsert()
    {
        Assert.Equal(new[] { "B", "C", "A" }, LauncherFavoriteOrder.Move(new[] { "A", "B", "C" }, 0, 2));
        Assert.Null(LauncherFavoriteOrder.Move(new[] { "A" }, 0, 3));
    }

    // ── model + prefs ───────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void LegacyConfig_PinsWithoutOrder_KeepRegistrationOrder()
    {
        var config = new InMemoryConfigSection();
        config.Set("pinned", new[] { "C", "A" });
        var reg = new LauncherRegistry(new LauncherPrefs(config), new StubLog());
        var m = new LauncherMenuModel(reg, () => _now);
        Register(reg, "A", "B", "C");
        Assert.Equal(new[] { "A", "C" }, Shown(m));
        Assert.Equal(new[] { "A", "C" }, Bar(m));
    }

    [Fact]
    public void NewFavorite_AppendsAtTheEnd_EvenOnALegacyConfig()
    {
        var config = new InMemoryConfigSection();
        config.Set("pinned", new[] { "B", "C" });
        var reg = new LauncherRegistry(new LauncherPrefs(config), new StubLog());
        var m = new LauncherMenuModel(reg, () => _now);
        var e = Register(reg, "A", "B", "C");
        m.ToggleFavorite(e[0]);   // A registered first, but a NEW favorite goes last
        Assert.Equal(new[] { "B", "C", "A" }, Shown(m));
        Assert.Equal(new[] { "B", "C", "A" }, new LauncherPrefs(config).FavoriteOrder);
    }

    [Fact]
    public void Reorder_InFullGrid_PersistsAndDrivesTheCompactBar()
    {
        var m = NewModel(out var reg, out var config);
        var e = Register(reg, "A", "B", "C");
        foreach (var x in e) m.ToggleFavorite(x);
        var r = m.MoveShown(2, 0);
        Assert.Equal("C", r!.Value.Entry.Title);
        Assert.Equal(1, r.Value.Position);
        Assert.Equal(new[] { "C", "A", "B" }, Shown(m));
        Assert.Equal(new[] { "C", "A", "B" }, Bar(m));
        Assert.Equal(new[] { "C", "A", "B" }, new LauncherPrefs(config).FavoriteOrder);
    }

    [Fact]
    public void Reorder_InCompactBar_UpdatesTheFavoritesTab()
    {
        var m = NewModel(out var reg, out _);
        var e = Register(reg, "A", "B", "C");
        foreach (var x in e) m.ToggleFavorite(x);
        Assert.Equal(2, m.MoveFavorite(0, 1)!.Value.Position);
        Assert.Equal(new[] { "B", "A", "C" }, Shown(m));
    }

    [Fact]
    public void Unfavorite_RemovesFromTheSavedOrder()
    {
        var m = NewModel(out var reg, out var config);
        var e = Register(reg, "A", "B");
        foreach (var x in e) m.ToggleFavorite(x);
        m.ToggleFavorite(e[0]);
        Assert.Equal(new[] { "B" }, new LauncherPrefs(config).FavoriteOrder);
    }

    [Fact]
    public void HiddenFavorite_KeepsItsSlotWhenTheVisibleOnesAreReordered()
    {
        var m = NewModel(out var reg, out var config);
        var visible = true;
        var a = Entry("A"); var h = Entry("H", () => visible); var c = Entry("C");
        reg.Register(a); reg.Register(h); reg.Register(c);
        m.ToggleFavorite(a); m.ToggleFavorite(h); m.ToggleFavorite(c);
        visible = false; _now += 1;   // H hides its tile (ShouldShow re-read)
        Assert.Equal(new[] { "A", "C" }, Shown(m));
        m.MoveShown(1, 0);
        Assert.Equal(new[] { "C", "H", "A" }, new LauncherPrefs(config).FavoriteOrder);
    }

    [Fact]
    public void Reorder_IsOffInAllTab_WithFindText_AndOnGhosts()
    {
        var m = NewModel(out var reg, out _);
        var e = Register(reg, Enumerable.Range(0, 18).Select(i => "P" + i).ToArray());   // > 16 → Find offered
        m.ToggleFavorite(e[0]); m.ToggleFavorite(e[1]); m.ToggleFavorite(e[2]);
        Assert.True(m.CanReorderShown(0));

        m.ToggleFavorite(e[1]);   // un-star in Favorites → ghost, keeps slot 1
        Assert.Equal(new[] { "P0", "P1", "P2" }, Shown(m));
        Assert.False(m.CanReorderShown(1));
        Assert.Null(m.MoveShown(1, 0));

        m.Query = "P";
        Assert.False(m.CanReorderShown(0));
        m.Query = "";
        m.SelectTab(LauncherTab.All);
        Assert.False(m.CanReorderShown(0));
    }

    [Fact]
    public void Ghost_StaysInPlaceAcrossAReorder_AndReStarringPutsItBackThere()
    {
        var m = NewModel(out var reg, out var config);
        var e = Register(reg, "A", "B", "C", "D");
        foreach (var x in e) m.ToggleFavorite(x);
        m.ToggleFavorite(e[1]);                         // B → ghost at slot 1
        m.MoveShown(3, 0);                              // D to the front: D A B(ghost) C
        Assert.Equal(new[] { "D", "A", "B", "C" }, Shown(m));
        m.ToggleFavorite(e[1]);                         // re-star B: back in its slot, not at the end
        Assert.Equal(new[] { "D", "A", "B", "C" }, new LauncherPrefs(config).FavoriteOrder);
    }
}
