using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>The Full launcher menu's two tabs. Persisted by <see cref="LauncherPrefs"/> (by name).</summary>
internal enum LauncherTab
{
    /// <summary>Only the favorited (= pinned) plugins — the same list the compact bar shows. The default.</summary>
    Favorites,

    /// <summary>Every plugin.</summary>
    All,
}

/// <summary>
/// The launcher's list logic, kept out of the (Unity) view so it is unit-testable: which plugins each tab shows,
/// the Find filter, and the Favorites "ghost" rule (2.30.0 launcher redesign).
/// <para>Lists: <see cref="AllCount"/> = every plugin tile whose live <see cref="LauncherEntry.ShouldShow"/> passes;
/// favorites = those that are pinned, in the user's saved order (favorites and the compact bar are ONE list; drag-reorder via <see cref="MoveShown"/> / <see cref="MoveFavorite"/>). <see cref="ShownAt"/> = the
/// current tab, narrowed by the Find text while the Find field is offered (more than <see cref="FindThreshold"/>
/// plugins).</para>
/// <para>Ghost: un-starring a tile while the Favorites tab is open keeps it in the grid (faded, see
/// <see cref="IsGhost"/>) until the tab changes, so the tile never vanishes from under the pointer. Re-starring it
/// makes it a normal tile again.</para>
/// </summary>
/// <remarks>
/// The view reads the lists by index for a fixed tile pool on every refresh (~10 Hz, up to 32 + 16 slots), so the
/// lists are cached and rebuilt only when something changed: a registry revision (register/unregister/pin), a tab /
/// query / ghost change here, or — because <see cref="LauncherEntry.ShouldShow"/> is a live pull (phase gates) — at
/// most every <see cref="RebuildSeconds"/>.
/// </remarks>
internal sealed class LauncherMenuModel
{
    /// <summary>The Find field is offered only above this many plugins.</summary>
    public const int FindThreshold = 16;

    /// <summary>Longest a cached list may go without re-reading the live ShouldShow gates.</summary>
    public const double RebuildSeconds = 0.1;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly LauncherRegistry _registry;
    private readonly Func<double> _now;
    private readonly List<LauncherEntry> _all = new();
    private readonly List<LauncherEntry> _favorites = new();
    private readonly List<LauncherEntry> _shown = new();
    private readonly List<LauncherEntry> _ghosts = new();   // by reference (LauncherEntry is a value-equal record)
    private readonly List<int> _ghostAt = new();            // each ghost's slot in the Favorites tab (parallel to _ghosts)
    private readonly List<LauncherEntry> _pinned = new();   // every registered pinned plugin, ShouldShow or not, in saved order
    private readonly List<LauncherEntry> _tabSeq = new();   // the Favorites tab before Find: visible favorites + ghosts
    private string _query = "";
    private int _builtRevision = -1;
    private int _state;          // bumped by every local change (tab, query, ghost)
    private int _builtState = -1;
    private double _builtAt;

    /// <param name="registry">The launcher entries + pin/tab prefs.</param>
    /// <param name="now">Seconds clock for the ShouldShow re-read (default: a process-wide stopwatch); tests pass their own.</param>
    public LauncherMenuModel(LauncherRegistry registry, Func<double>? now = null)
    {
        _registry = registry;
        _now = now ?? (() => Clock.Elapsed.TotalSeconds);
    }

    /// <summary>The open tab (remembered across sessions).</summary>
    public LauncherTab Tab => _registry.Tab;

    /// <summary>Opens <paramref name="tab"/>. Any tab click ends the Favorites ghosts (they disappear now).</summary>
    public void SelectTab(LauncherTab tab)
    {
        _registry.Tab = tab;
        _ghosts.Clear(); _ghostAt.Clear();
        _state++;   // always: the cached tab list must rebuild now, not at the next ShouldShow re-read
    }

    /// <summary>Drops the faded un-starred tiles (tab change, mode change).</summary>
    public void ClearGhosts()
    {
        if (_ghosts.Count == 0) return;
        _ghosts.Clear(); _ghostAt.Clear();
        _state++;
    }

    /// <summary>The Find text (whatever the user typed; matched case-insensitively against the shown name).</summary>
    public string Query
    {
        get => _query;
        set
        {
            value ??= "";
            if (string.Equals(value, _query, StringComparison.Ordinal)) return;
            _query = value;
            _state++;
        }
    }

    /// <summary>True when the Find field is offered (more than <see cref="FindThreshold"/> plugins).</summary>
    public bool FindVisible { get { Refresh(); return _all.Count > FindThreshold; } }

    /// <summary>True when the Find text narrows the grid (field offered and not blank).</summary>
    public bool QueryActive => FindVisible && _query.Trim().Length > 0;

    /// <summary>Every visible plugin (the "All plugins" count and the header's plugin count).</summary>
    public int AllCount { get { Refresh(); return _all.Count; } }

    /// <summary>Visible favorites (the Favorites count, the header's favorite count and the compact bar).</summary>
    public int FavoriteCount { get { Refresh(); return _favorites.Count; } }

    /// <summary>Tiles in the current tab after the Find filter.</summary>
    public int ShownCount { get { Refresh(); return _shown.Count; } }

    /// <summary>The <paramref name="index"/>-th tile of the current tab (null past the end).</summary>
    public LauncherEntry? ShownAt(int index) { Refresh(); return At(_shown, index); }

    /// <summary>The <paramref name="index"/>-th favorite in the user's order (the compact bar).</summary>
    public LauncherEntry? FavoriteAt(int index) { Refresh(); return At(_favorites, index); }

    /// <summary>Favorites tab with nothing to show and no Find text → the "No favorites yet" hint.</summary>
    public bool FavoritesEmpty => Tab == LauncherTab.Favorites && ShownCount == 0 && !QueryActive;

    /// <summary>The Find text matches nothing in the current tab → the "No plugin matches" hint.</summary>
    public bool NoMatch => QueryActive && ShownCount == 0;

    public bool IsFavorite(LauncherEntry entry) => _registry.IsPinned(entry);

    /// <summary>An un-starred tile still kept (faded) in the Favorites tab.</summary>
    public bool IsGhost(LauncherEntry entry) => IndexOfGhost(entry) >= 0 && !_registry.IsPinned(entry);

    /// <summary>Stars / un-stars <paramref name="entry"/> and returns its NEW favorite state. Un-starring while the
    /// Favorites tab is open turns the tile into a ghost that keeps its slot; starring it again makes it a normal tile
    /// in that same slot. Any other new favorite goes to the END of the order (which also writes the order the first
    /// time, so an older config's registration order is kept as the base).</summary>
    public bool ToggleFavorite(LauncherEntry entry)
    {
        Refresh();
        var favorite = !_registry.IsPinned(entry);
        var g = IndexOfGhost(entry);
        if (favorite)
        {
            var full = Titles(_pinned);
            List<string> order;
            if (g >= 0)
            {
                // Back into its ghost slot: the Favorites tab's pinned tiles + this one, in their on-screen order.
                var subset = new List<string>(_tabSeq.Count);
                foreach (var e in _tabSeq)
                    if (ReferenceEquals(e, entry) || _registry.IsPinned(e)) subset.Add(e.Title);
                order = LauncherFavoriteOrder.Apply(full, subset);
                _ghosts.RemoveAt(g); _ghostAt.RemoveAt(g);
            }
            else
            {
                order = full;
                if (!order.Contains(entry.Title)) order.Add(entry.Title);
            }
            _registry.SetPinned(entry, true);
            _registry.SetFavoriteOrder(order);
        }
        else
        {
            if (Tab == LauncherTab.Favorites && g < 0)
            {
                var at = IndexOfRef(_tabSeq, entry);
                _ghosts.Add(entry); _ghostAt.Add(at < 0 ? int.MaxValue : at);
            }
            _registry.SetPinned(entry, false);
        }
        _state++;
        return favorite;
    }

    /// <summary>True when the Full grid tile <paramref name="index"/> may be dragged to reorder: Favorites tab only, a
    /// real favorite (not a ghost), and no Find text (a filtered grid hides tiles, so where a drop lands in the full
    /// order would be a guess — reordering is simply off while Find narrows the grid).</summary>
    public bool CanReorderShown(int index)
        => Tab == LauncherTab.Favorites && !QueryActive && ShownAt(index) is { } e && _registry.IsPinned(e);

    /// <summary>Full grid drop (<c>OnReorder</c>: remove-then-insert indices over the shown tiles). Ghosts keep the
    /// slots they end up in. Returns the moved favorite and its new 1-based position among the favorites, or null when
    /// the move is not allowed.</summary>
    public (LauncherEntry Entry, int Position)? MoveShown(int from, int to)
    {
        if (!CanReorderShown(from)) return null;
        var moved = LauncherFavoriteOrder.Move(_shown, from, to);
        if (moved == null) return null;
        for (var k = 0; k < _ghosts.Count; k++) _ghostAt[k] = IndexOfRef(moved, _ghosts[k]);
        var subset = new List<LauncherEntry>(moved.Count);
        foreach (var e in moved) if (_registry.IsPinned(e)) subset.Add(e);
        return Commit(_shown[from], subset);
    }

    /// <summary>Compact bar drop over the visible favorites. Same result shape as <see cref="MoveShown"/>.</summary>
    public (LauncherEntry Entry, int Position)? MoveFavorite(int from, int to)
    {
        Refresh();
        var moved = LauncherFavoriteOrder.Move(_favorites, from, to);
        return moved == null ? null : Commit(_favorites[from], moved);
    }

    // Writes the visible favorites' new order into the full saved order (hidden favorites keep their slots).
    private (LauncherEntry, int) Commit(LauncherEntry entry, List<LauncherEntry> visibleInNewOrder)
    {
        _registry.SetFavoriteOrder(LauncherFavoriteOrder.Apply(Titles(_pinned), Titles(visibleInNewOrder)));
        _state++;
        return (entry, IndexOfRef(visibleInNewOrder, entry) + 1);
    }

    /// <summary>The name a tile shows: the entry's live display title, or its stable title if that throws (plugin
    /// code must never break the launcher).</summary>
    public static string NameOf(LauncherEntry entry)
    {
        try { return entry.DisplayTitle ?? entry.Title; }
        catch { return entry.Title; }
    }

    private void Refresh()
    {
        var now = _now();
        if (_builtRevision == _registry.Revision && _builtState == _state && now - _builtAt < RebuildSeconds) return;
        _builtRevision = _registry.Revision;
        _builtState = _state;
        _builtAt = now;

        _all.Clear(); _favorites.Clear(); _shown.Clear(); _tabSeq.Clear(); _pinned.Clear();
        var pinnedInRegistration = new List<LauncherEntry>();
        foreach (var e in _registry.Entries)
        {
            if (e.Group != LauncherGroup.Plugin) continue;
            if (_registry.IsPinned(e)) pinnedInRegistration.Add(e);
            if (Shows(e)) _all.Add(e);
        }
        // Favorites (and the compact bar) follow the user's saved order; All plugins stays in registration order.
        _pinned.AddRange(LauncherFavoriteOrder.Sort(pinnedInRegistration, e => e.Title, _registry.FavoriteOrder));
        foreach (var e in _pinned) if (IndexOfRef(_all, e) >= 0) _favorites.Add(e);

        // Favorites tab: the favorites, with each ghost back in the slot it had (lowest slot first).
        _tabSeq.AddRange(_favorites);
        for (var pass = 0; pass < _ghosts.Count; pass++)
        {
            var k = -1;
            for (var j = 0; j < _ghosts.Count; j++)
                if (IndexOfRef(_tabSeq, _ghosts[j]) < 0 && IndexOfRef(_all, _ghosts[j]) >= 0
                    && !_registry.IsPinned(_ghosts[j]) && (k < 0 || _ghostAt[j] < _ghostAt[k])) k = j;
            if (k < 0) break;
            _tabSeq.Insert(Math.Min(Math.Max(_ghostAt[k], 0), _tabSeq.Count), _ghosts[k]);
        }

        var query = _all.Count > FindThreshold ? _query.Trim() : "";
        foreach (var e in Tab == LauncherTab.All ? _all : _tabSeq)
        {
            if (query.Length > 0 && NameOf(e).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _shown.Add(e);
        }
    }

    // Live phase/state gate (LauncherEntry.ShouldShow); a throwing gate shows the tile rather than breaking the menu.
    private static bool Shows(LauncherEntry e)
    {
        try { return e.ShouldShow?.Invoke() ?? true; }
        catch { return true; }
    }

    private int IndexOfGhost(LauncherEntry entry) => IndexOfRef(_ghosts, entry);

    // By reference: LauncherEntry is a value-equal record.
    private static int IndexOfRef(List<LauncherEntry> list, LauncherEntry entry)
    {
        for (var i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], entry)) return i;
        return -1;
    }

    private static List<string> Titles(List<LauncherEntry> list)
    {
        var r = new List<string>(list.Count);
        foreach (var e in list) r.Add(e.Title);
        return r;
    }

    private static LauncherEntry? At(List<LauncherEntry> list, int index)
        => index >= 0 && index < list.Count ? list[index] : null;
}
