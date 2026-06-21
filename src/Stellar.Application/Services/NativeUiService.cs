using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Manages per-resolution position/visibility state for the native UI
/// allowlist. Holds resolved handles; re-asserts saved rects on a 1 Hz
/// cadence to defeat per-frame Panda updates. Persists per (id × resolution)
/// flat keys through a single <see cref="IConfigSection"/>.
/// </summary>
/// <remarks>
/// Ownership: <see cref="OnFrameworkDispose"/> restores every modified
/// element to its OriginalRect so unloading the framework leaves the game's
/// native UI in its original positions (mod isolation).
/// </remarks>
internal sealed class NativeUiService
{
    private const float ReassertIntervalSeconds = 1.0f;
    private const float ResolveRetrySeconds     = 5.0f;

    private readonly INativeUiAdapter _adapter;
    private readonly IConfigSection _config;
    private readonly Func<int> _activeSlot;
    private readonly IPluginLog _log;
    private readonly Dictionary<string, EntryState> _entries = new();
    private float _reassertAccumulator;
    private float _resolveAccumulator;
    private Resolution _lastResolution;

    public NativeUiService(INativeUiAdapter adapter, IConfigSection config, Func<int> activeSlot,
                            IPluginLog log, IReadOnlyList<NativeUiEntryDescriptor> targets)
    {
        _adapter = adapter;
        _config = config;
        _activeSlot = activeSlot;
        _log = log;
        foreach (var t in targets)
            _entries[t.Id] = new EntryState(t);
        _log.Info($"[NativeUi] tracking {_entries.Count} allowlist entries");
    }

    public IReadOnlyCollection<EntryState> Entries => _entries.Values;

    /// <summary>The entry's CURRENT curated screen rect (live), for the edit-mode outline + hit-test — so the
    /// box tracks the element's real size/position (e.g. the party panel growing 5→20 person) rather than the
    /// rect snapshotted at resolve.</summary>
    public WindowRect GetLiveRect(EntryState e) => e.IsResolved ? _adapter.GetCurrentRect(e.Handle) : e.Rect;

    /// <summary>All resolved native-UI entries the editor can outline — INCLUDING hidden ones (so they keep a
    /// dimmed re-enable outline). Rect is the live curated rect; CanHide mirrors the allowlist SafeToHide flag.</summary>
    public IEnumerable<EditableElement> EditableElements()
    {
        foreach (var e in _entries.Values)
            if (e.IsResolved)
                yield return new EditableElement(e.Descriptor.Id, GetLiveRect(e), e.Visible, e.Descriptor.SafeToHide);
    }

    public void Tick(float deltaTime, Resolution currentRes)
    {
        _lastResolution = currentRes;
        _reassertAccumulator += deltaTime;
        _resolveAccumulator += deltaTime;

        if (_resolveAccumulator >= ResolveRetrySeconds)
        {
            _resolveAccumulator = 0f;
            TryResolveAll(currentRes);
        }
        if (_reassertAccumulator >= ReassertIntervalSeconds)
        {
            _reassertAccumulator = 0f;
            ReassertAll();
        }
    }

    public void SetVisible(string id, bool visible)
    {
        if (!_entries.TryGetValue(id, out var e) || !e.IsResolved) return;
        if (!e.Descriptor.SafeToHide && !visible)
        {
            _log.Warning($"[NativeUi] '{id}' is not safe-to-hide; ignoring request.");
            return;
        }
        _adapter.SetVisible(e.Handle, visible);
        e.Visible = visible;
        e.IsModified = true;
        Persist(e);
    }

    /// <summary>
    /// Apply a new rect to the live element WITHOUT persisting — called every
    /// frame during a drag. Persisting (a disk write) is deferred to
    /// <see cref="Commit"/> on drag-release, matching the mod-window behaviour.
    /// </summary>
    public void SetRect(string id, WindowRect rect)
    {
        if (!_entries.TryGetValue(id, out var e) || !e.IsResolved) return;
        _adapter.SetRect(e.Handle, rect);
        e.Rect = rect;
        e.IsModified = true;
    }

    /// <summary>Persist the current rect/visibility for <paramref name="id"/> — call on drag-release.</summary>
    public void Commit(string id)
    {
        if (_entries.TryGetValue(id, out var e) && e.IsResolved) Persist(e);
    }

    /// <summary>Forward a diagnostic rect dump to the adapter (self-gates on diagnostics; called on edit-mode
    /// enter to investigate oversized outlines — bug #5).</summary>
    public void DumpRectDiagnostics(Action<string> log) => _adapter.DumpDiagnostics(log);

    public void ResetToOriginal(string id)
    {
        if (!_entries.TryGetValue(id, out var e) || !e.IsResolved) return;
        // Full original-pose writeback (anchors/pivot/size/pos/active), not a
        // SetRect round-trip — SetRect only translates anchoredPosition, so a
        // reset-via-SetRect could leave an element the game has since nudged.
        _adapter.RestoreOriginal(e.Handle);
        e.Rect = e.Handle.OriginalRect;
        e.Visible = true;
        e.IsModified = false;
        Persist(e);
    }

    public void ResetAll()
    {
        foreach (var e in _entries.Values)
            if (e.IsResolved) ResetToOriginal(e.Descriptor.Id);
    }

    /// <summary>
    /// Re-apply the active layout slot's saved native-UI positions to every
    /// already-resolved entry. Called on slot-switch (LayoutEditorOverlay) so a
    /// layout slot restores the game HUD arrangement alongside the mod windows.
    /// Entries with no override in the new slot snap back to the game's original
    /// pose.
    /// </summary>
    public void ReapplyForActiveSlot(Resolution res)
    {
        _lastResolution = res;
        var slot = _activeSlot();
        foreach (var e in _entries.Values)
        {
            if (!e.IsResolved) continue;
            _adapter.RestoreOriginal(e.Handle);
            e.IsModified = false;
            if (TryLoadFor(slot, e.Descriptor.Id, res, out var rect, out var visible))
            {
                _adapter.SetRect(e.Handle, rect);
                _adapter.SetVisible(e.Handle, visible);
                e.Rect = rect;
                e.Visible = visible;
                e.IsModified = true;
            }
            else
            {
                e.Rect = e.Handle.OriginalRect;
                e.Visible = true;
            }
        }
    }

    /// <summary>
    /// Mod-isolation cleanup — invoke <see cref="INativeUiAdapter.RestoreOriginal"/>
    /// for every resolved entry. This writes back the full captured original
    /// pose (anchors, pivot, size, anchoredPos, activeSelf), NOT just the
    /// screen rect — <see cref="SetRect"/> re-anchors to (0,1) and a
    /// reset-via-SetRect would leave the anchoring mod-injected even though
    /// the visible position matches.
    /// </summary>
    public void OnFrameworkDispose()
    {
        foreach (var e in _entries.Values)
        {
            if (!e.IsResolved) continue;
            _adapter.RestoreOriginal(e.Handle);
        }
    }

    private void TryResolveAll(Resolution res)
    {
        foreach (var e in _entries.Values)
        {
            // Self-heal on scene change: the game destroys + rebuilds these HUD nodes, leaving our cached handle
            // pointing at a dead object — so a resolved entry that's no longer alive must be re-resolved and have
            // its saved layout re-applied to the NEW element (else the move "doesn't survive a scene change").
            if (e.IsResolved)
            {
                if (_adapter.IsAlive(e.Handle)) continue;
                _log.Info($"[NativeUi] '{e.Descriptor.Id}' went stale (scene change?) — re-resolving");
                e.IsResolved = false;
            }
            if (!_adapter.TryResolve(e.Descriptor.Path, e.Descriptor.RectChild, out var handle)) continue;
            e.Handle = handle;
            e.IsResolved = true;
            _log.Info($"[NativeUi] resolved '{e.Descriptor.Id}'");
            if (TryLoadFor(_activeSlot(), e.Descriptor.Id, res, out var savedRect, out var savedVisible))
            {
                _adapter.SetRect(handle, savedRect);
                _adapter.SetVisible(handle, savedVisible);
                e.Rect = savedRect;
                e.Visible = savedVisible;
                e.IsModified = true;
            }
            else
            {
                // No user override: leave the element exactly where the game
                // placed it. Do NOT apply OriginalRect — only re-assert poses
                // the user has actually changed (see ReassertAll).
                e.Rect = handle.OriginalRect;
                e.Visible = true;
            }
        }
    }

    private void ReassertAll()
    {
        foreach (var e in _entries.Values)
        {
            // Only defend elements the user has actually customized. Re-asserting
            // an unmodified element's OriginalRect is not a guaranteed no-op for
            // deeply-nested HUD nodes and was displacing them on resolve.
            if (!e.IsResolved || !e.IsModified) continue;
            // Re-assert POSITION only (SetRect is a no-op when the value hasn't drifted, and self-skips while the
            // element is game-hidden during a cutscene — see PandaHudAdapter.SetRect). Do NOT force visibility on:
            // the game owns show/hide (it hides the HUD during cutscenes), and a 1 Hz SetVisible(true) fought that,
            // flickering elements on mid-cutscene. We only ENFORCE a user-requested hide here; un-hiding is handled
            // by the explicit SetVisible path + re-resolve reapply.
            _adapter.SetRect(e.Handle, e.Rect);
            if (!e.Visible) _adapter.SetVisible(e.Handle, false);
        }
    }

    // Cache of the 5 flat config keys per (entry × resolution). Built lazily
    // on first read; the (id, resolution) pair is stable for the entire
    // session so the dictionary never grows beyond ~9 × N-resolutions
    // entries. Previous impl reallocated the 5 strings every 5s resolve pass
    // for every unresolved entry.
    private readonly Dictionary<(int, string, Resolution), string[]> _configKeyCache = new();

    private string[] GetConfigKeys(int slot, string id, Resolution res)
    {
        var key = (slot, id, res);
        if (_configKeyCache.TryGetValue(key, out var cached)) return cached;
        var prefix = ConfigPrefix(slot, id, res);
        var keys = new[]
        {
            $"{prefix}.x", $"{prefix}.y", $"{prefix}.w", $"{prefix}.h", $"{prefix}.visible",
        };
        _configKeyCache[key] = keys;
        return keys;
    }

    private bool TryLoadFor(int slot, string id, Resolution res, out WindowRect rect, out bool visible)
    {
        var keys = GetConfigKeys(slot, id, res);
        // Sentinel: if "<prefix>.x" doesn't exist (returns sentinel default), no saved state.
        const float NotPersisted = float.MinValue;
        var x = _config.Get(keys[0], NotPersisted);
        if (x <= NotPersisted) { rect = default; visible = true; return false; }
        var y = _config.Get(keys[1], 0f);
        // Position-only: a native element is MOVED, never resized — its size is recomputed live from the
        // curated rect each frame (GetCurrentRect), and SetRect translates by the live size. So we persist only
        // the top-left; width/height carry 0 (ignored downstream). This also means a later rect-curation tweak
        // takes effect immediately without a Reset (no stale saved size to mask it).
        rect = new WindowRect(x, y, 0f, 0f);
        visible = _config.Get(keys[4], true);
        return true;
    }

    private void Persist(EntryState e)
    {
        // Use the last Tick'd resolution as the persistence key. Edits made
        // before the first Tick (cannot happen — Settings UI is gated on
        // post-OnGUI frames) would use a default Resolution; safe enough.
        var keys = GetConfigKeys(_activeSlot(), e.Descriptor.Id, _lastResolution);
        // Position-only (see TryLoadFor): persist top-left + visibility; size is live, never saved.
        _config.Set(keys[0], e.Rect.X);
        _config.Set(keys[1], e.Rect.Y);
        _config.Set(keys[4], e.Visible);
        _config.Save();
    }

    // Native HUD positions are scoped per layout slot (slot0 = Default), so a
    // layout slot captures the whole screen arrangement — mod windows AND game
    // UI. Slot-switch reapplies via ReapplyForActiveSlot.
    private static string ConfigPrefix(int slot, string id, Resolution res)
        => $"slot{slot}.{id}.{res.Width}x{res.Height}";

    internal sealed class EntryState
    {
        public EntryState(NativeUiEntryDescriptor d) { Descriptor = d; }
        public NativeUiEntryDescriptor Descriptor { get; }
        public bool IsResolved { get; set; }
        public NativeUiHandle Handle { get; set; }
        public WindowRect Rect { get; set; }
        public bool Visible { get; set; } = true;
        // True once the user moves/hides this element (or a saved override is
        // loaded). Gates ReassertAll so untouched game UI is never written.
        public bool IsModified { get; set; }
    }
}
