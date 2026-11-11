using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Persistence half of <see cref="NativeUiService"/>. Keys (all in the "gameui" section):
/// <list type="bullet">
/// <item><c>slot{n}.{id}.{W}x{H}.ox/.oy/.visible</c> — the override: wrapper offset (wrapper-parent local units,
///   y-up) + shown flag, per layout slot × resolution.</item>
/// <item><c>slot{n}.{id}.{W}x{H}.x/.y</c> — LEGACY (pre-wrapper) saved screen top-left. Read only to migrate (once
///   the element is seen at a settled size the offset is computed + written); never deleted by this change.</item>
/// <item><c>seen.{id}.{W}x{H}.x/.y/.w/.h/.s</c> — last-seen DEFAULT (offset-0) curated rect in screen px + screen px
///   per local unit, per resolution (screen px don't transfer between resolutions), not per slot (it's the game's
///   geometry, not the user's layout). Drives edit-mode placeholders. Reset leaves it (it isn't an override).</item>
/// </list>
/// Closest-resolution reuse within 10% and the unconfigured-resolution fallback mirror LayoutStorage.
/// </summary>
internal sealed partial class NativeUiService
{
    private const int KOx = 0, KOy = 1, KVisible = 2, KLegacyX = 3, KLegacyY = 4, KMigration = 5;

    // Written with every offset save. 2 = the offset was computed by a build that measures an INNER wrapper only once it
    // holds the element's content. An inner-wrapped entry with an offset but no marker (saved by f8a7de57 or earlier,
    // which could migrate against an EMPTY wrapper — the class-gauge bug) re-migrates once from its legacy .x/.y.
    private const float MigrationVersion = 2f;
    private const int KSeenX = 0, KSeenY = 1, KSeenW = 2, KSeenH = 3, KSeenS = 4;
    private const float NotPersisted = float.MinValue;

    // 10% of the larger screen axis — the max resolution mismatch we'll reuse a saved pose across. Mirrors
    // LayoutStorage.ResolutionMatchDeltaFraction so the native-UI path matches the mod-window / HUD path.
    private const double ResolutionMatchDeltaFraction = 0.10;

    // Flat key strings per (slot × id × resolution) and per (id × resolution), built once — the pairs are stable
    // for a session, so no key string is ever rebuilt on a load/persist.
    private readonly Dictionary<(int, string, Resolution), string[]> _configKeyCache = new();
    private readonly Dictionary<(string, Resolution), string[]> _seenKeyCache = new();

    private readonly struct SavedState
    {
        public SavedState(bool hasOffset, float ox, float oy, WindowRect? legacy, bool visible)
        { HasOffset = hasOffset; Ox = ox; Oy = oy; Legacy = legacy; Visible = visible; }
        public bool HasOffset { get; }
        public float Ox { get; }
        public float Oy { get; }
        public WindowRect? Legacy { get; }
        public bool Visible { get; }
    }

    // Load (slot × id × res) into the entry and push it to the game. See ReapplyForActiveSlot for the two modes.
    private void LoadState(EntryState e, Resolution res, bool applyVisibility)
    {
        LoadSeen(e, res);
        e.HasMeasure = false;
        if (TryLoadFor(_activeSlot(), e.Descriptor.Id, res, out var s))
        {
            e.OffsetX = s.HasOffset ? s.Ox : 0f;
            e.OffsetY = s.HasOffset ? s.Oy : 0f;
            e.LegacyRect = s.HasOffset ? null : s.Legacy;
            if (applyVisibility) e.Visible = s.Visible;
            e.IsModified = true;
            ApplyToGame(e);
        }
        else
            ClearOverride(e, visible: applyVisibility || e.Visible);
        e.Loaded = true;
    }

    private bool TryLoadFor(int slot, string id, Resolution res, out SavedState state)
    {
        if (TryLoadExact(slot, id, res, out state)) return true;
        // Exact-resolution miss — fall back to the CLOSEST saved resolution within delta (LayoutStorage parity), so a
        // transient/changed resolution read during a scene load can't strand a customized element at the default.
        // Requires the config to enumerate keys (IConfigKeyReader); degrades to no-fallback if not.
        if (_config is IConfigKeyReader reader
            && FindClosestSavedResolution(reader, slot, id, res) is { } cres
            && TryLoadExact(slot, id, cres, out state)) return true;
        state = default;
        return false;
    }

    private bool TryLoadExact(int slot, string id, Resolution res, out SavedState state)
    {
        var k = GetConfigKeys(slot, id, res);
        var visible = _config.Get(k[KVisible], true);
        var ox = _config.Get(k[KOx], NotPersisted);
        if (ox > NotPersisted && !NeedsRemigration(id, k))
        {
            state = new SavedState(true, ox, _config.Get(k[KOy], 0f), null, visible);
            return true;
        }
        var x = _config.Get(k[KLegacyX], NotPersisted);
        if (x > NotPersisted)
        {
            state = new SavedState(false, 0f, 0f, new WindowRect(x, _config.Get(k[KLegacyY], 0f), 0f, 0f), visible);
            return true;
        }
        state = default;
        return false;
    }

    // See MigrationVersion: an untrusted inner-wrapper offset that still has its legacy rect to migrate from.
    private bool NeedsRemigration(string id, string[] k)
        => _entries.TryGetValue(id, out var e) && e.Descriptor.Wrap == NativeUiWrapMode.Inner
        && _config.Get(k[KMigration], 0f) < MigrationVersion && _config.Get(k[KLegacyX], NotPersisted) > NotPersisted;

    private void Persist(EntryState e)
    {
        // Keyed by the last Tick'd resolution (edits only happen in-world, long after the first Tick).
        var k = GetConfigKeys(_activeSlot(), e.Descriptor.Id, _lastResolution);
        _config.Set(k[KOx], e.OffsetX);
        _config.Set(k[KOy], e.OffsetY);
        _config.Set(k[KVisible], e.Visible);
        _config.Set(k[KMigration], MigrationVersion);
        WriteSeen(e);
        _config.Save();
    }

    private void LoadSeen(EntryState e, Resolution res)
    {
        var k = GetSeenKeys(e.Descriptor.Id, res);
        var x = _config.Get(k[KSeenX], NotPersisted);
        e.HasSeen = x > NotPersisted;
        e.SeenFresh = e.SeenDirty = false;
        if (!e.HasSeen) return;
        e.Seen = new SeenGeometry
        {
            X = x, Y = _config.Get(k[KSeenY], 0f), W = _config.Get(k[KSeenW], 0f), H = _config.Get(k[KSeenH], 0f),
            PxPerUnit = _config.Get(k[KSeenS], 0f),
        };
    }

    // Persist dirty last-seen geometry (at most one quiet save per measure pass; usually once per element per session).
    private void FlushSeen()
    {
        var any = false;
        foreach (var e in _order)
            if (e.SeenDirty) { WriteSeen(e); any = true; }
        if (any) _config.SaveQuiet();
    }

    private void WriteSeen(EntryState e)
    {
        if (!e.SeenDirty || !e.HasSeen) return;
        var k = GetSeenKeys(e.Descriptor.Id, _lastResolution);
        _config.Set(k[KSeenX], e.Seen.X);
        _config.Set(k[KSeenY], e.Seen.Y);
        _config.Set(k[KSeenW], e.Seen.W);
        _config.Set(k[KSeenH], e.Seen.H);
        _config.Set(k[KSeenS], e.Seen.PxPerUnit);
        e.SeenDirty = false;
    }

    private string[] GetConfigKeys(int slot, string id, Resolution res)
    {
        var key = (slot, id, res);
        if (_configKeyCache.TryGetValue(key, out var cached)) return cached;
        // Native HUD state is scoped per layout slot (slot0 = Default): a slot captures the whole screen — mod
        // windows AND game UI. Slot-switch reapplies via ReapplyForActiveSlot.
        var p = $"slot{slot}.{id}.{res.Width}x{res.Height}";
        var keys = new[] { $"{p}.ox", $"{p}.oy", $"{p}.visible", $"{p}.x", $"{p}.y", $"{p}.mv" };
        _configKeyCache[key] = keys;
        return keys;
    }

    private string[] GetSeenKeys(string id, Resolution res)
    {
        var key = (id, res);
        if (_seenKeyCache.TryGetValue(key, out var cached)) return cached;
        var p = $"seen.{id}.{res.Width}x{res.Height}";
        var keys = new[] { $"{p}.x", $"{p}.y", $"{p}.w", $"{p}.h", $"{p}.s" };
        _seenKeyCache[key] = keys;
        return keys;
    }

    // Nearest saved resolution (within delta) for this (slot × id), or null. Parses the "{W}x{H}" segment out of the
    // persisted "slot{slot}.{id}.{W}x{H}.{field}" keys — the prefix carries the id's own dots + a trailing dot, so it
    // can't collide with a longer id sharing this stem (same reasoning as ResetToOriginal's prefix).
    private static Resolution? FindClosestSavedResolution(IConfigKeyReader reader, int slot, string id, Resolution target)
    {
        var prefix = $"slot{slot}.{id}.";
        Resolution? best = null;
        var bestDist = double.MaxValue;
        var maxDelta = Math.Max(target.Width, target.Height) * ResolutionMatchDeltaFraction;
        foreach (var key in reader.KeysWithPrefix(prefix))
        {
            var rest = key.Substring(prefix.Length);          // "{W}x{H}.{field}"
            var dot = rest.IndexOf('.');
            if (dot <= 0) continue;
            if (!TryParseResolution(rest.Substring(0, dot), out var savedRes)) continue;
            var dist = savedRes.DistanceTo(target);
            if (dist < bestDist && dist <= maxDelta) { bestDist = dist; best = savedRes; }
        }
        return best;
    }

    private static bool TryParseResolution(string segment, out Resolution res)
    {
        res = default;
        var parts = segment.Split('x');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h)) return false;
        res = new Resolution(w, h);
        return true;
    }
}
