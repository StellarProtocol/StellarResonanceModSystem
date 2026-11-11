using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Per-resolution move/hide state for the native UI allowlist. A customized element is owned through a framework
/// WRAPPER (see <see cref="INativeUiAdapter"/>): its offset + hide live on our RectTransform, so the game's own
/// writes to its node compose with ours instead of fighting them — there is no re-assert loop any more. The tick
/// only (a) re-wraps when a scene change rebuilt the element and (b) resolves absent elements at a low rate.
/// Persists per (slot × id × resolution) flat keys through a single <see cref="IConfigSection"/>.
/// </summary>
/// <remarks>
/// Ownership: <see cref="OnFrameworkDispose"/> unwraps every element so unloading the framework leaves the game's
/// HUD hierarchy exactly as we found it (mod isolation). Untouched elements are never wrapped.
/// </remarks>
internal sealed partial class NativeUiService
{
    private readonly INativeUiAdapter _adapter;
    private readonly IConfigSection _config;
    private readonly Func<int> _activeSlot;
    private readonly IPluginLog _log;
    private readonly Dictionary<string, EntryState> _entries = new();
    private readonly EntryState[] _order;   // stable iteration order without a dictionary enumerator per pass
    private Resolution _lastResolution;

    public NativeUiService(INativeUiAdapter adapter, IConfigSection config, Func<int> activeSlot,
                            IPluginLog log, IReadOnlyList<NativeUiEntryDescriptor> targets)
    {
        _adapter = adapter;
        _config = config;
        _activeSlot = activeSlot;
        _log = log;
        _order = new EntryState[targets.Count];
        for (var i = 0; i < targets.Count; i++)
            _entries[targets[i].Id] = _order[i] = new EntryState(targets[i]);
        _log.Info($"[NativeUi] tracking {_entries.Count} allowlist entries");
    }

    public IReadOnlyCollection<EntryState> Entries => _entries.Values;

    public void SetVisible(string id, bool visible)
    {
        if (!_entries.TryGetValue(id, out var e) || !IsOffered(e)) return;
        if (!e.Descriptor.SafeToHide && !visible)
        {
            _log.Warning($"[NativeUi] '{id}' is not safe-to-hide; ignoring request.");
            return;
        }
        e.Visible = visible;
        e.IsModified = true;
        ApplyToGame(e);
        Persist(e);
    }

    /// <summary>
    /// Move the element so its curated rect's top-left lands at <paramref name="rect"/> (screen px, top-left /
    /// y-down) WITHOUT persisting — called every frame during a drag; <see cref="Commit"/> persists on release.
    /// The screen delta from the element's current rect becomes a wrapper-offset delta in the wrapper parent's
    /// local units. Works on a placeholder too (absent / game-hidden element): the offset applies when it appears.
    /// </summary>
    public void SetRect(string id, WindowRect rect)
    {
        if (!_entries.TryGetValue(id, out var e) || !IsOffered(e)) return;
        var cur = GetLiveRect(e);
        var clamped = ClampFullyOnScreen(new WindowRect(rect.X, rect.Y, cur.Width, cur.Height), _lastResolution);
        var (dox, doy) = ScreenDeltaToOffset(clamped.X - cur.X, clamped.Y - cur.Y, PxPerUnit(e));
        e.OffsetX += dox;
        e.OffsetY += doy;
        e.LegacyRect = null;   // an explicit user move supersedes a pending legacy-rect migration
        e.IsModified = true;
        e.Rect = clamped;
        // Keep the cached measurement in step so the outline tracks the drag this tick (the 10 Hz re-measure
        // would otherwise lag it).
        if (e.HasMeasure) e.LastMeasure = e.LastMeasure with { Rect = clamped };
        ApplyToGame(e);
    }

    /// <summary>Persist the current offset/visibility for <paramref name="id"/> — call on drag-release.</summary>
    public void Commit(string id)
    {
        if (_entries.TryGetValue(id, out var e)) Persist(e);
    }

    /// <summary>Forward a diagnostic rect dump to the adapter (self-gates on diagnostics; called on edit-mode
    /// enter to investigate oversized outlines — bug #5).</summary>
    public void DumpRectDiagnostics(Action<string> log) => _adapter.DumpDiagnostics(log);

    public void ResetToOriginal(string id)
    {
        if (!_entries.TryGetValue(id, out var e)) return;
        ClearOverride(e, visible: true);
        // Reset = drop the saved override entirely (matches LayoutStorage.Remove for mod windows). Remove every
        // persisted key for this id under the active slot across ALL resolutions (prefix has a trailing dot, so it
        // can't collide with a longer id sharing this stem) → every resolution falls back to the game default.
        _config.RemoveByPrefix($"slot{_activeSlot()}.{id}.");
        _config.Save();
    }

    public void ResetAll()
    {
        foreach (var e in _order) ResetToOriginal(e.Descriptor.Id);
    }

    /// <summary>
    /// Re-load the active layout slot's saved state for <paramref name="res"/> into every entry and push it to the
    /// game. Called on slot-switch (applyVisibility=true: saved show/hide too; no override → back to default and
    /// shown) and on a resolution change (applyVisibility=false: position only; an unconfigured resolution falls
    /// back to the game default = offset 0, while the current visibility is kept).
    /// </summary>
    public void ReapplyForActiveSlot(Resolution res, bool applyVisibility = true)
    {
        _lastResolution = res;
        foreach (var e in _order) LoadState(e, res, applyVisibility);
    }

    /// <summary>Mod-isolation cleanup — unwrap every resolved element (outer: element back to its original parent
    /// + sibling index; inner: children back to the root), leaving the game hierarchy untouched.</summary>
    public void OnFrameworkDispose()
    {
        foreach (var e in _order)
            if (e.IsResolved) _adapter.Unwrap(e.Handle);
    }

    /// <summary>
    /// Force every entry to re-resolve on the next <see cref="Tick"/>. Called from the Host on a canvas-generation
    /// bump (scene change rebuilt the game HUD) — a still-alive element re-binds to its cached handle (wrapper
    /// intact → Apply is a no-op write), a rebuilt one is re-wrapped with the in-memory offset immediately.
    /// </summary>
    internal void InvalidateResolvedHandles()
    {
        foreach (var e in _order) e.IsResolved = false;
        RequestRecheck();
    }

    /// <summary>Arm an immediate liveness + resolve pass on the next <see cref="Tick"/> — wired to the scene-enter
    /// signals (<c>OnEnterScene</c>, <c>HudMgr.OnEnterScene</c>/<c>Init</c>). O(1): sets a flag.</summary>
    internal void RequestRecheck() => _recheckArmed = true;

    // Drop the override in memory and return the element to the game default (unwrapped).
    private void ClearOverride(EntryState e, bool visible)
    {
        e.OffsetX = e.OffsetY = 0f;
        e.LegacyRect = null;
        e.Visible = visible;
        e.IsModified = !visible;   // a kept hide is still a customization (wrapped at offset 0, scale 0)
        ApplyToGame(e);
    }

    // Push the in-memory state to the game: customized → wrapper offset + hide; untouched → no wrapper at all. The
    // editor's transient "Hide others" (NativeUiService.EditHide.cs) wraps an untouched element at offset 0 (pixel-
    // identical) just to scale it to 0, and only ever ANDs the effective visibility — e.Visible itself is never changed.
    private void ApplyToGame(EntryState e)
    {
        if (!e.IsResolved) return;   // applied when it resolves
        if (NeedsWrap(e)) _adapter.Apply(e.Handle, e.OffsetX, e.OffsetY, e.Visible && !_transientHidden);
        else _adapter.Unwrap(e.Handle);
    }

    private bool NeedsWrap(EntryState e) => e.IsModified || _transientHidden;

    internal sealed class EntryState
    {
        public EntryState(NativeUiEntryDescriptor d) { Descriptor = d; }
        public NativeUiEntryDescriptor Descriptor { get; }
        public bool IsResolved { get; set; }
        public NativeUiHandle Handle { get; set; }
        /// <summary>Last rect shown for this element in the editor (screen px, top-left / y-down).</summary>
        public WindowRect Rect { get; set; }
        public bool Visible { get; set; } = true;
        /// <summary>True once the user moves/hides this element (or a saved override is loaded). Only these are
        /// wrapped — untouched game UI is never written.</summary>
        public bool IsModified { get; set; }
        /// <summary>Wrapper offset in the wrapper parent's local units (y-up), the persisted position.</summary>
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }

        internal bool Loaded;                 // state loaded from config for the current slot × resolution
        internal WindowRect? LegacyRect;      // pre-wrapper saved screen top-left awaiting migration to an offset
        internal SeenGeometry Seen;           // last-seen default geometry at the current resolution (placeholders)
        internal bool HasSeen, SeenFresh, SeenDirty;
        internal SeenStability Stability;     // the settle run a measured rect must survive before it becomes Seen
        internal float LivePx;                // px per unit from the latest SHOWING measurement (0 = none yet)
        internal NativeUiMeasure LastMeasure;
        internal float LastMeasureAt;
        internal bool HasMeasure;
        internal bool WrapLossLogged;         // one "wrapper lost" log line per loss, not per retry
    }

    /// <summary>Last-seen geometry of an element at one resolution: its DEFAULT (offset-0) curated top-left and
    /// size in screen px, plus screen px per wrapper-local unit — enough to draw a placeholder at the saved offset
    /// while the element is absent or game-hidden.</summary>
    internal struct SeenGeometry
    {
        public float X, Y, W, H, PxPerUnit;
    }

    /// <summary>The current run of matching default-rect measurements: the run's FIRST rect (every later sample is
    /// compared to it, so a slow fade-shrink can't creep along in sub-tolerance steps) and when the run started.</summary>
    internal struct SeenStability
    {
        public WindowRect Rect;
        public float Since;
        public bool Has;
    }
}
