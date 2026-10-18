using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

// Persistence layer — uses IConfigSection's Get<T>/Set<T> with strings since the
// current config layer doesn't ship structured JSON support. We serialize the
// slot data as a single flat-key prefix scheme: "slots.<slotIdx>.name" +
// "slots.<slotIdx>.windows.<windowId>.<resKey>.{x,y,w,h,visible}". The slot-INDEPENDENT
// bucket uses a parallel "global.windows.<windowId>.<resKey>.*" scheme with its own
// "global.index" (no slot dimension) — distinct from every "slots.N.*" key.
//
// Trade-off: hand-rolled flat-key format works against the existing IConfigSection
// contract without changes to it. If IConfigSection later gains JSON support, we
// collapse this to a single JSON blob.
internal sealed partial class LayoutStorage
{
    private const string GlobalIndexKey    = "global.index";
    private const string GlobalWindowPrefix = "global.windows";

    private List<SlotData> LoadSlots(int count)
    {
        var slots = new List<SlotData>(count);
        for (var i = 0; i < count; i++)
        {
            slots.Add(new SlotData
            {
                Name = _section.Get($"slots.{i}.name", DefaultSlotName(i)) ?? DefaultSlotName(i),
                Windows = LoadWindows($"slots.{i}.index", $"slots.{i}.windows"),
            });
        }
        return slots;
    }

    // Slot-independent bucket (no slot dimension): read once at construction, mirroring LoadSlots' per-slot read.
    private Dictionary<string, Dictionary<string, WindowState>> LoadGlobalWindows()
        => LoadWindows(GlobalIndexKey, GlobalWindowPrefix);

    // The config layer doesn't enumerate; we read by-key. Window IDs and resolution keys are tracked via an
    // index list ("id@resKey;id@resKey;..."). Shared by the per-slot and global buckets (only the key prefixes differ).
    private Dictionary<string, Dictionary<string, WindowState>> LoadWindows(string indexKey, string windowPrefix)
    {
        var index = _section.Get(indexKey, string.Empty) ?? string.Empty;
        var result = new Dictionary<string, Dictionary<string, WindowState>>();
        if (string.IsNullOrEmpty(index)) return result;

        foreach (var pair in index.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = pair.Split('@');
            if (bits.Length != 2) continue;
            var windowId = bits[0];
            var resKey = bits[1];

            var state = ReadWindowState($"{windowPrefix}.{windowId}.{resKey}");
            if (!result.TryGetValue(windowId, out var perRes))
            {
                perRes = new Dictionary<string, WindowState>();
                result[windowId] = perRes;
            }
            perRes[resKey] = state;
        }
        return result;
    }

    private WindowState ReadWindowState(string baseKey)
    {
        var x = _section.Get($"{baseKey}.x", 0f);
        var y = _section.Get($"{baseKey}.y", 0f);
        var w = _section.Get($"{baseKey}.w", 0f);
        var h = _section.Get($"{baseKey}.h", 0f);
        var v = _section.Get($"{baseKey}.visible", true);
        // 2.22.0 sidecar (rollback-safe: older builds read x/y/w/h only and ignore these keys).
        (float, float)? anchor = _section.Get($"{baseKey}.anc", false)
            ? (_section.Get($"{baseKey}.ax", 0f), _section.Get($"{baseKey}.ay", 0f))
            : null;
        return new WindowState(new WindowRect(x, y, w, h), v, anchor);
    }

    private void PersistSlots()
    {
        _section.Set(SlotCountKey, _slots.Count);
        for (var i = 0; i < _slots.Count; i++)
        {
            _section.Set($"slots.{i}.name", _slots[i].Name);
            WriteWindows($"slots.{i}.index", $"slots.{i}.windows", _slots[i].Windows);
        }
        _section.Save();
    }

    // Slot-independent bucket persist: writes the global.* keys only, then saves. Its own Save() so a global-window
    // drag doesn't rewrite every slot's keys (and vice-versa).
    private void PersistGlobal()
    {
        WriteWindows(GlobalIndexKey, GlobalWindowPrefix, _global);
        _section.Save();
    }

    private void WriteWindows(string indexKey, string windowPrefix, Dictionary<string, Dictionary<string, WindowState>> windows)
    {
        var indexEntries = windows
            .SelectMany(kvp => kvp.Value.Keys.Select(rk => $"{kvp.Key}@{rk}"))
            .ToList();
        _section.Set(indexKey, string.Join(";", indexEntries));

        foreach (var (windowId, perRes) in windows)
            foreach (var (resKey, state) in perRes)
                WriteWindowState($"{windowPrefix}.{windowId}.{resKey}", state);
    }

    private void WriteWindowState(string baseKey, WindowState state)
    {
        _section.Set($"{baseKey}.x",       state.Rect.X);
        _section.Set($"{baseKey}.y",       state.Rect.Y);
        _section.Set($"{baseKey}.w",       state.Rect.Width);
        _section.Set($"{baseKey}.h",       state.Rect.Height);
        _section.Set($"{baseKey}.visible", state.Visible);
        // Always written, so a later save without an offset clears a stale one (anc=false).
        _section.Set($"{baseKey}.anc",     state.Anchor is not null);
        _section.Set($"{baseKey}.ax",      state.Anchor?.X ?? 0f);
        _section.Set($"{baseKey}.ay",      state.Anchor?.Y ?? 0f);
    }

    private static string DefaultSlotName(int i) => i == 0 ? "Default" : $"Slot {i + 1}";

    private sealed class SlotData
    {
        public string Name { get; set; } = "";
        public Dictionary<string, Dictionary<string, WindowState>> Windows { get; set; } = new();
    }

    /// <summary><paramref name="Anchor"/> = the anchor offset in design units (2.22.0); null = a legacy / anchor-less save.</summary>
    internal readonly record struct WindowState(WindowRect Rect, bool Visible, (float X, float Y)? Anchor);
}
