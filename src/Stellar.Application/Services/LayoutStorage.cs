using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed partial class LayoutStorage
{
    private const string SectionName = "ui.layout";
    private const string ActiveSlotKey = "active_slot";
    private const string SlotCountKey  = "slot_count";
    private const string SnapThresholdKey = "snap_threshold_px";
    private const string SnapEnabledKey   = "snap_enabled";
    private const int    DefaultSlotCount = 4;
    private const int    MinSlotCount = 2;
    private const int    MaxSlotCount = 8;
    private const float  DefaultSnapThreshold = 6f;
    private const float  MinSnapThreshold = 0f;
    private const float  MaxSnapThreshold = 24f;
    private const double ResolutionMatchDeltaFraction = 0.10;   // 10%

    private readonly IConfigSection _section;
    private readonly IPluginLog _log;
    private readonly List<SlotData> _slots;
    // Slot-INDEPENDENT window layouts: one bucket keyed by (windowId → resKey), with NO slot dimension, persisted
    // under the "global.*" key prefix. Windows whose spec sets SlotIndependentLayout (e.g. the layout-edit toolbar)
    // store their position/visibility here, so a slot switch never moves them. A separate map (not a reserved slot
    // index) because slots are a user-editable list with add/remove — a reserved index would shift under them.
    private readonly Dictionary<string, Dictionary<string, WindowState>> _global;
    private int _activeSlot;
    private float _snapThresholdPx;
    private bool _snapEnabled;

    public LayoutStorage(IPluginConfig config, IPluginLog log)
    {
        _section = config.GetSection(SectionName);
        _log = log;
        var persistedCount = Math.Clamp(_section.Get(SlotCountKey, DefaultSlotCount), MinSlotCount, MaxSlotCount);
        _slots = LoadSlots(persistedCount);
        _global = LoadGlobalWindows();
        _activeSlot = Math.Clamp(_section.Get(ActiveSlotKey, 0), 0, _slots.Count - 1);
        _snapThresholdPx = Math.Clamp(_section.Get(SnapThresholdKey, DefaultSnapThreshold), MinSnapThreshold, MaxSnapThreshold);
        _snapEnabled     = _section.Get(SnapEnabledKey, true);
        _log.Info($"[Layout] loaded {_slots.Count} slots, snap={(_snapEnabled ? "on" : "off")} threshold={_snapThresholdPx:0}px");
    }

    public int SlotCount => _slots.Count;
    public int ActiveSlot => _activeSlot;

    public float SnapThresholdPx
    {
        get => _snapThresholdPx;
        set
        {
            var clamped = Math.Clamp(value, MinSnapThreshold, MaxSnapThreshold);
            if (Math.Abs(clamped - _snapThresholdPx) < 0.01f) return;
            _snapThresholdPx = clamped;
            _section.Set(SnapThresholdKey, clamped);
            _section.Save();
        }
    }

    public bool SnapEnabled
    {
        get => _snapEnabled;
        set
        {
            if (value == _snapEnabled) return;
            _snapEnabled = value;
            _section.Set(SnapEnabledKey, value);
            _section.Save();
        }
    }

    public IReadOnlyList<string> SlotNames
    {
        get
        {
            if (_cachedSlotNames is not null) return _cachedSlotNames;
            var names = new string[_slots.Count];
            for (var i = 0; i < _slots.Count; i++) names[i] = _slots[i].Name;
            _cachedSlotNames = names;
            return names;
        }
    }

    // Snapshot of SlotNames that LayoutPanel + LayoutEditorOverlay read every
    // OnGUI pass. Rebuilt only when the slot list mutates (Rename / Add /
    // Remove); invalidated in those paths before SlotsChanged fires so the
    // Rename buffer refresh handler sees fresh names.
    private IReadOnlyList<string>? _cachedSlotNames;

    private void InvalidateSlotNamesCache() => _cachedSlotNames = null;

    public event Action? SlotsChanged;

    public string GetSlotName(int slot) => InRange(slot) ? _slots[slot].Name : "?";

    public (WindowRect Rect, bool Visible) Get(int slot, string windowId, Resolution resolution, WindowRect defaultRect, float scaleFactor = 1f)
        => GetFrom(InRange(slot) ? _slots[slot].Windows : null, windowId, resolution, defaultRect, scaleFactor);

    /// <summary>Slot-INDEPENDENT read: the window's layout from the single global bucket (no slot dimension).
    /// Mirrors <see cref="Get(int,string,Resolution,WindowRect,float)"/> for windows whose spec opts in.</summary>
    public (WindowRect Rect, bool Visible) GetGlobal(string windowId, Resolution resolution, WindowRect defaultRect, float scaleFactor = 1f)
        => GetFrom(_global, windowId, resolution, defaultRect, scaleFactor);

    private (WindowRect Rect, bool Visible) GetFrom(Dictionary<string, Dictionary<string, WindowState>>? windows, string windowId, Resolution resolution, WindowRect defaultRect, float scaleFactor)
    {
        // Saved rects are CANVAS units (screen ÷ scaleFactor on a CanvasScaler'd canvas); the clamp bound must be
        // canvas units too, else a non-1.0 scaleFactor shifts the window on every load. The KEY lookup stays raw.
        var clampRes = scaleFactor > 0f && System.Math.Abs(scaleFactor - 1f) > 0.001f
            ? new Resolution((int)System.Math.Round(resolution.Width / scaleFactor), (int)System.Math.Round(resolution.Height / scaleFactor))
            : resolution;
        if (windows is null || !windows.TryGetValue(windowId, out var perRes)) return (ClampToScreen(defaultRect, clampRes), true);

        // A layout saved for THIS exact resolution is the user's deliberate placement — honour edge-tucking, but
        // a rect dragged FULLY off-screen (0 px visible) is never intentional and was unrecoverable: it persists
        // at the exact resolution, so this exact-match path restored it off-screen on every boot (a window dragged
        // past the edge vanished "forever", e.g. CombatMeter). ClampVisible keeps ≥MinVisible px grabbable while
        // still allowing a deliberate tuck, so the placement is trusted as far as it can be without losing it.
        if (perRes.TryGetValue(resolution.Key, out var exact))
        {
            return (ClampVisible(exact.Rect, clampRes), exact.Visible);
        }

        // Reused from the closest other resolution within delta — keep it on-screen for this one.
        var closest = FindClosestResolution(perRes.Keys, resolution);
        if (closest is { } closestKey && perRes.TryGetValue(closestKey, out var c))
        {
            return (ClampToScreen(c.Rect, clampRes), c.Visible);
        }

        return (ClampToScreen(defaultRect, clampRes), true);
    }

    /// <summary>True iff a saved layout for this window (exact resolution, or the closest within delta) exists AND
    /// records it HIDDEN. Lets Register seed Visible=false for a user-hidden window without force-showing untouched
    /// ones (whose Get() default is visible=true).</summary>
    public bool IsPersistedHidden(int slot, string windowId, Resolution resolution)
        => InRange(slot) && IsPersistedHiddenIn(_slots[slot].Windows, windowId, resolution);

    /// <summary>Slot-INDEPENDENT variant of <see cref="IsPersistedHidden"/> (reads the global bucket).</summary>
    public bool IsPersistedHiddenGlobal(string windowId, Resolution resolution)
        => IsPersistedHiddenIn(_global, windowId, resolution);

    private bool IsPersistedHiddenIn(Dictionary<string, Dictionary<string, WindowState>> windows, string windowId, Resolution resolution)
    {
        if (!windows.TryGetValue(windowId, out var perRes)) return false;
        if (perRes.TryGetValue(resolution.Key, out var exact)) return !exact.Visible;
        var closest = FindClosestResolution(perRes.Keys, resolution);
        return closest is { } k && perRes.TryGetValue(k, out var c) && !c.Visible;
    }

    // Keep a window reachable on the current screen: a DefaultRect tuned for a larger resolution (or a layout
    // reused from a bigger screen) can place a window past the right/bottom edge — off-screen and ungrabbable.
    // Pull the top-left back so a fixed-size window fits fully on-screen, or — for content-auto-sized windows
    // (Width/Height 0) — keep at least MinVisible px of each axis grabbable. Applied only to fallback
    // placements, never to a layout the user saved for THIS resolution.
    private static WindowRect ClampToScreen(WindowRect rect, Resolution res)
    {
        const float MinVisible = 80f;
        var w = rect.Width  > 0f ? rect.Width  : MinVisible;
        var h = rect.Height > 0f ? rect.Height : MinVisible;
        var x = Math.Clamp(rect.X, 0f, Math.Max(0f, res.Width  - w));
        var y = Math.Clamp(rect.Y, 0f, Math.Max(0f, res.Height - h));
        return new WindowRect(x, y, rect.Width, rect.Height);
    }

    /// <summary>Gentler than <see cref="ClampToScreen"/>: guarantees only that a grabbable band of the window
    /// stays on-screen while still allowing the user to tuck most of it past an edge. Used for exact-resolution
    /// (user-placed) saves and shared with the live drag clamp (<c>WindowInteractionTicker</c>) so the two agree.
    /// The protected affordance is the top/handle edge: the top is never pushed above the screen top (so a
    /// titlebar — or a whole-frame-draggable overlay's grab band — stays reachable). A bottom-right resize grip is
    /// intentionally sacrificable: a window tucked to the bottom keeps its handle but may push the grip off-screen
    /// (drag it back up, or "Reset selected", to recover it).</summary>
    public const float MinVisiblePx = 80f;

    public static WindowRect ClampVisible(WindowRect rect, Resolution res)
    {
        var w = rect.Width > 0f ? rect.Width : MinVisiblePx;
        // Keep a band on-screen at either edge. For windows narrower than the band the whole width is the band,
        // so a narrow window can still flush to either screen edge (vis-w then goes to 0, not positive).
        var vis = Math.Min(w, MinVisiblePx);
        var x = Math.Clamp(rect.X, vis - w, Math.Max(vis - w, res.Width - vis));
        var y = Math.Clamp(rect.Y, 0f, Math.Max(0f, res.Height - MinVisiblePx));
        return new WindowRect(x, y, rect.Width, rect.Height);
    }

    /// <summary>Drop the saved override for one window in a slot so it falls back to its DefaultRect (clamped
    /// on-screen) on the next mount. Backs the layout-editor "Reset selected"/"Reset all" for mod windows + HUDs.</summary>
    public void Remove(int slot, string windowId)
    {
        if (!InRange(slot)) return;
        if (_slots[slot].Windows.Remove(windowId)) PersistSlots();
    }

    /// <summary>Slot-INDEPENDENT variant of <see cref="Remove"/> (drops the global override).</summary>
    public void RemoveGlobal(string windowId)
    {
        if (_global.Remove(windowId)) PersistGlobal();
    }

    /// <summary>The anchor offset saved with this window's layout (exact resolution first, else the closest one within
    /// delta — an offset is resolution-independent), and whether a LEGACY exact-resolution save (no offset: saved before
    /// 2.22.0, or by a path that does not know the anchor) is all there is.</summary>
    public bool TryGetAnchorOffset(int slot, string windowId, Resolution resolution, out (float X, float Y) offset, out bool legacyExact)
    {
        offset = default;
        legacyExact = false;
        return InRange(slot) && TryGetAnchorOffsetIn(_slots[slot].Windows, windowId, resolution, out offset, out legacyExact);
    }

    /// <summary>Slot-INDEPENDENT variant of <see cref="TryGetAnchorOffset"/> (reads the global bucket).</summary>
    public bool TryGetAnchorOffsetGlobal(string windowId, Resolution resolution, out (float X, float Y) offset, out bool legacyExact)
    {
        offset = default;
        legacyExact = false;
        return TryGetAnchorOffsetIn(_global, windowId, resolution, out offset, out legacyExact);
    }

    private bool TryGetAnchorOffsetIn(Dictionary<string, Dictionary<string, WindowState>> windows, string windowId, Resolution resolution, out (float X, float Y) offset, out bool legacyExact)
    {
        offset = default;
        legacyExact = false;
        if (!windows.TryGetValue(windowId, out var perRes)) return false;
        if (perRes.TryGetValue(resolution.Key, out var exact))
        {
            if (exact.Anchor is { } a) { offset = a; return true; }
            legacyExact = true;
            return false;
        }
        var closest = FindClosestResolution(perRes.Keys, resolution);
        if (closest is { } k && perRes.TryGetValue(k, out var c) && c.Anchor is { } ca) { offset = ca; return true; }
        return false;
    }

    public void Save(int slot, string windowId, Resolution resolution, WindowRect rect, bool visible) =>
        Save(slot, windowId, resolution, new WindowState(rect, visible, null));

    /// <summary>Saves a window's rect, visibility and (2.22.0) anchor offset (<see cref="AnchoredPlacement.OffsetOf"/>,
    /// design units).</summary>
    public void Save(int slot, string windowId, Resolution resolution, WindowState state)
    {
        if (!InRange(slot))
        {
            _log.Warning($"[Layout] save ignored: slot {slot} out of range [0,{_slots.Count - 1}]");
            return;
        }
        SaveInto(_slots[slot].Windows, windowId, resolution, state);
        PersistSlots();
    }

    /// <summary>Slot-INDEPENDENT variant of <see cref="Save(int,string,Resolution,WindowRect,bool)"/>.</summary>
    public void SaveGlobal(string windowId, Resolution resolution, WindowRect rect, bool visible) =>
        SaveGlobal(windowId, resolution, new WindowState(rect, visible, null));

    /// <summary>Slot-INDEPENDENT variant of <see cref="Save(int,string,Resolution,WindowState)"/> (writes the global bucket).</summary>
    public void SaveGlobal(string windowId, Resolution resolution, WindowState state)
    {
        SaveInto(_global, windowId, resolution, state);
        PersistGlobal();
    }

    private static void SaveInto(Dictionary<string, Dictionary<string, WindowState>> windows, string windowId, Resolution resolution, WindowState state)
    {
        if (!windows.TryGetValue(windowId, out var perRes))
        {
            perRes = new Dictionary<string, WindowState>();
            windows[windowId] = perRes;
        }
        perRes[resolution.Key] = state;
    }

    public void SetActiveSlot(int slot)
    {
        if (!InRange(slot))
        {
            _log.Warning($"[Layout] active slot {slot} out of range [0,{_slots.Count - 1}]");
            return;
        }
        _activeSlot = slot;
        _section.Set(ActiveSlotKey, slot);
        _section.Save();
        // Fire SlotsChanged so LayoutPanel's rename-buffer refresh handler
        // runs — without this, switching slots from the floating toolbar
        // leaves the Layout panel showing the previous slot's name in the
        // rename input. Mn3 in the Phase 9a review.
        SlotsChanged?.Invoke();
    }

    public void ResetSlot(int slot)
    {
        if (!InRange(slot)) return;
        _slots[slot].Windows.Clear();
        PersistSlots();
    }

    public void RenameSlot(int slot, string name)
    {
        if (!InRange(slot)) return;
        _slots[slot].Name = string.IsNullOrWhiteSpace(name) ? DefaultSlotName(slot) : name;
        InvalidateSlotNamesCache();
        PersistSlots();
        SlotsChanged?.Invoke();
    }

    public void AddSlot()
    {
        if (_slots.Count >= MaxSlotCount) return;
        var idx = _slots.Count;
        _slots.Add(new SlotData
        {
            Name = $"Slot {idx + 1}",
            Windows = new Dictionary<string, Dictionary<string, WindowState>>(),
        });
        _section.Set(SlotCountKey, _slots.Count);
        InvalidateSlotNamesCache();
        PersistSlots();
        SlotsChanged?.Invoke();
    }

    public void RemoveSlot(int slot)
    {
        if (_slots.Count <= MinSlotCount) return;
        if (!InRange(slot)) return;
        _slots.RemoveAt(slot);
        if (_activeSlot >= _slots.Count) _activeSlot = _slots.Count - 1;
        _section.Set(SlotCountKey, _slots.Count);
        _section.Set(ActiveSlotKey, _activeSlot);
        InvalidateSlotNamesCache();
        PersistSlots();
        SlotsChanged?.Invoke();
    }

    private bool InRange(int slot) => slot >= 0 && slot < _slots.Count;

    private string? FindClosestResolution(IEnumerable<string> savedKeys, Resolution target)
    {
        string? bestKey = null;
        double bestDist = double.MaxValue;
        var maxDelta = Math.Max(target.Width, target.Height) * ResolutionMatchDeltaFraction;

        foreach (var key in savedKeys)
        {
            if (!TryParseKey(key, out var savedRes)) continue;
            var dist = savedRes.DistanceTo(target);
            if (dist < bestDist && dist <= maxDelta)
            {
                bestDist = dist;
                bestKey = key;
            }
        }

        return bestKey;
    }

    private static bool TryParseKey(string key, out Resolution resolution)
    {
        resolution = default;
        var parts = key.Split('x');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h)) return false;
        resolution = new Resolution(w, h);
        return true;
    }

}
