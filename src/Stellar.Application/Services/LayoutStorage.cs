using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed class LayoutStorage
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
    private int _activeSlot;
    private float _snapThresholdPx;
    private bool _snapEnabled;

    public LayoutStorage(IPluginConfig config, IPluginLog log)
    {
        _section = config.GetSection(SectionName);
        _log = log;
        var persistedCount = Math.Clamp(_section.Get(SlotCountKey, DefaultSlotCount), MinSlotCount, MaxSlotCount);
        _slots = LoadSlots(persistedCount);
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

    public (WindowRect Rect, bool Visible) Get(int slot, string windowId, Resolution resolution, WindowRect defaultRect)
    {
        if (!InRange(slot)) return (ClampToScreen(defaultRect, resolution), true);
        var slotData = _slots[slot];
        if (!slotData.Windows.TryGetValue(windowId, out var perRes)) return (ClampToScreen(defaultRect, resolution), true);

        // A layout saved for THIS exact resolution is the user's deliberate placement — honour edge-tucking, but
        // a rect dragged FULLY off-screen (0 px visible) is never intentional and was unrecoverable: it persists
        // at the exact resolution, so this exact-match path restored it off-screen on every boot (a window dragged
        // past the edge vanished "forever", e.g. CombatMeter). ClampVisible keeps ≥MinVisible px grabbable while
        // still allowing a deliberate tuck, so the placement is trusted as far as it can be without losing it.
        if (perRes.TryGetValue(resolution.Key, out var exact))
        {
            return (ClampVisible(exact.Rect, resolution), exact.Visible);
        }

        // Reused from the closest other resolution within delta — keep it on-screen for this one.
        var closest = FindClosestResolution(perRes.Keys, resolution);
        if (closest is { } closestKey && perRes.TryGetValue(closestKey, out var c))
        {
            return (ClampToScreen(c.Rect, resolution), c.Visible);
        }

        return (ClampToScreen(defaultRect, resolution), true);
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

    public void Save(int slot, string windowId, Resolution resolution, WindowRect rect, bool visible)
    {
        if (!InRange(slot))
        {
            _log.Warning($"[Layout] save ignored: slot {slot} out of range [0,{_slots.Count - 1}]");
            return;
        }

        var slotData = _slots[slot];
        if (!slotData.Windows.TryGetValue(windowId, out var perRes))
        {
            perRes = new Dictionary<string, WindowState>();
            slotData.Windows[windowId] = perRes;
        }
        perRes[resolution.Key] = new WindowState(rect, visible);
        PersistSlots();
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

    // Persistence layer — uses IConfigSection's Get<T>/Set<T> with strings since the
    // current config layer doesn't ship structured JSON support. We serialize the
    // slot data as a single flat-key prefix scheme: "slots.<slotIdx>.name" +
    // "slots.<slotIdx>.windows.<windowId>.<resKey>.{x,y,w,h,visible}".
    //
    // Trade-off: hand-rolled flat-key format works against the existing IConfigSection
    // contract without changes to it. If IConfigSection later gains JSON support, we
    // collapse this to a single JSON blob.

    private List<SlotData> LoadSlots(int count)
    {
        var slots = new List<SlotData>(count);
        for (var i = 0; i < count; i++)
        {
            slots.Add(new SlotData
            {
                Name = _section.Get($"slots.{i}.name", DefaultSlotName(i)) ?? DefaultSlotName(i),
                Windows = LoadWindowsForSlot(i),
            });
        }
        return slots;
    }

    private Dictionary<string, Dictionary<string, WindowState>> LoadWindowsForSlot(int slot)
    {
        // The config layer doesn't enumerate; we read by-key. Window IDs and
        // resolution keys are tracked via an index list.
        var index = _section.Get($"slots.{slot}.index", string.Empty) ?? string.Empty;
        var result = new Dictionary<string, Dictionary<string, WindowState>>();
        if (string.IsNullOrEmpty(index)) return result;

        foreach (var pair in index.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = pair.Split('@');
            if (bits.Length != 2) continue;
            var windowId = bits[0];
            var resKey = bits[1];

            var baseKey = $"slots.{slot}.windows.{windowId}.{resKey}";
            var x = _section.Get($"{baseKey}.x", 0f);
            var y = _section.Get($"{baseKey}.y", 0f);
            var w = _section.Get($"{baseKey}.w", 0f);
            var h = _section.Get($"{baseKey}.h", 0f);
            var v = _section.Get($"{baseKey}.visible", true);

            if (!result.TryGetValue(windowId, out var perRes))
            {
                perRes = new Dictionary<string, WindowState>();
                result[windowId] = perRes;
            }
            perRes[resKey] = new WindowState(new WindowRect(x, y, w, h), v);
        }
        return result;
    }

    private void PersistSlots()
    {
        _section.Set(SlotCountKey, _slots.Count);
        for (var i = 0; i < _slots.Count; i++)
        {
            var slotData = _slots[i];
            _section.Set($"slots.{i}.name", slotData.Name);

            var indexEntries = slotData.Windows
                .SelectMany(kvp => kvp.Value.Keys.Select(rk => $"{kvp.Key}@{rk}"))
                .ToList();
            _section.Set($"slots.{i}.index", string.Join(";", indexEntries));

            foreach (var (windowId, perRes) in slotData.Windows)
            {
                foreach (var (resKey, state) in perRes)
                {
                    var baseKey = $"slots.{i}.windows.{windowId}.{resKey}";
                    _section.Set($"{baseKey}.x",       state.Rect.X);
                    _section.Set($"{baseKey}.y",       state.Rect.Y);
                    _section.Set($"{baseKey}.w",       state.Rect.Width);
                    _section.Set($"{baseKey}.h",       state.Rect.Height);
                    _section.Set($"{baseKey}.visible", state.Visible);
                }
            }
        }
        _section.Save();
    }

    private static string DefaultSlotName(int i) => i == 0 ? "Default" : $"Slot {i + 1}";

    private sealed class SlotData
    {
        public string Name { get; set; } = "";
        public Dictionary<string, Dictionary<string, WindowState>> Windows { get; set; } = new();
    }

    private readonly record struct WindowState(WindowRect Rect, bool Visible);
}
