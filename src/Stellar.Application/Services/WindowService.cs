using System;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Runtime for the uGUI interactive window toolkit. Mounts windows when the canvas is present,
/// re-applies display values at a capped ~10 Hz (or immediately on MarkDirty), self-heals on scene-change
/// destroys. Gating + persistence + focus→keyboard-gate signal land in sibling partials (Plans 3–4).</summary>
internal sealed partial class WindowService : IWindowHost
{
    private const float ApplyInterval = 0.1f;
    private readonly IWindowRenderer _renderer;
    private readonly IPluginLog _log;
    private readonly IGameMenuState _menuState;
    private readonly IClientState _clientState;
    private readonly Dictionary<string, Entry> _windows = new();
    private float _accum;

    public WindowService(IWindowRenderer renderer, IPluginLog log, IGameMenuState menuState, IClientState clientState)
    { _renderer = renderer; _log = log; _menuState = menuState; _clientState = clientState; }

    public IWindowControl Register(WindowRegistration reg)
    {
        if (_windows.TryGetValue(reg.Spec.Id, out var existing))
        {
            // A live duplicate is a bug. But a just-Remove()'d entry (not yet pruned) is the rebuild pattern
            // (Remove()+Register() same frame, e.g. StatInspector's column-count change) — reclaim it now
            // instead of no-op'ing, else the re-register silently returns a NoOpHandle and the window vanishes.
            if (!existing.Removed) { _log.Error($"[Window] duplicate id '{reg.Spec.Id}'; ignored."); return new NoOpHandle(); }
            DestroyIfMounted(existing);
        }
        var e = new Entry(reg) { Owner = this }.Init(reg.Spec.StartVisible); _windows[reg.Spec.Id] = e; return e;
    }

    public IWindowControl Register(WindowRegistration registration, HotkeyAction toggleAction, IHotkeys hotkeys)
    {
        var control = Register(registration);
        hotkeys.DeclareAction(toggleAction, () => control.SetVisible(!control.IsShown));
        return control;
    }

    public IWindowControl? Find(string id) => _windows.TryGetValue(id, out var e) ? e : null;

    /// <summary>Editor-facing: edit-mode-movable windows currently shown, with their screen rects — so the
    /// layout editor can outline + label them like HUDs. Includes the overlay/status chromes
    /// (Tracker/Party/PillStatus) AND any window flagged <see cref="WindowSpec.EditModeDragOnly"/> (e.g. the
    /// CombatMeter, a Borderless overlay that only moves in edit mode). GlassMenu/Borderless free-drag popup
    /// dialogs (Settings/DataInspector/launcher) are intentionally excluded.</summary>
    internal IEnumerable<(string Id, WindowRect Rect)> EditableRects()
    {
        foreach (var e in EditableElements())
            if (e.Visible) yield return (e.Id, e.Rect);
    }

    /// <summary>Editor enumeration — every EditModeDragOnly window incl. hidden, with state. Rect is live when
    /// shown, else the last-saved / default rect (so a hidden window keeps a re-enable outline). Always hideable.
    /// (A Party-chromed free-drag dialog like CombatMeter History is excluded — only EditModeDragOnly windows.)</summary>
    internal IEnumerable<EditableElement> EditableElements()
    {
        foreach (var kv in _windows)
        {
            var e = kv.Value;
            if (e.Removed || !e.Reg.Spec.EditModeDragOnly) continue;
            var rect = e.Token != null ? _renderer.GetRect(e.Token)
                     : e.LastSavedRect.Width > 0 ? e.LastSavedRect : e.Reg.Spec.DefaultRect;
            yield return new EditableElement(kv.Key, rect, e.Visible, CanHide: true);
        }
    }

    /// <summary>True if any mounted window holds keyboard focus in a text field — drives the keyboard gate
    /// (the Host OR-combines this into InputCaptureService / KeyboardInputGate each frame). Cheap per-frame.</summary>
    public bool AnyFieldFocused
    {
        get
        {
            foreach (var e in _windows.Values)
                if (!e.Removed && e.Token != null && _renderer.HasFocusedField(e.Token)) return true;
            return false;
        }
    }

    public void Tick(float deltaSeconds)
    {
        _accum += deltaSeconds;
        var applyNow = _accum >= ApplyInterval;
        if (applyNow) _accum = 0f;
        foreach (var e in _windows.Values) if (!e.Removed) TickEntry(e, applyNow);
        PruneRemoved();
    }

    private void TickEntry(Entry e, bool applyNow)
    {
        if (!e.Visible) { DestroyIfMounted(e); return; }
        if (e.Token is null || !_renderer.IsAlive(e.Token))
        {
            if (!_renderer.IsCanvasAvailable()) { e.Token = null; return; }
            e.Token = SafeMount(e);
            if (e.Token is null) return;
            ApplySavedRect(e);   // restore saved position (or DefaultRect) — WindowService.Layout
            SafeApply(e);
            e.Dirty = false; return;
        }
        if (applyNow || e.Dirty) { SafeApply(e); e.Dirty = false; }
        if (applyNow) PersistIfSettled(e);   // save the rect once a titlebar drag settles
    }

    private object? SafeMount(Entry e)
    { try { return _renderer.Mount(e.Reg); } catch (Exception ex) { _log.Warning($"[Window/{e.Reg.Spec.Id}] mount: {ex.Message}"); return null; } }
    private void SafeApply(Entry e)
    {
        // Auto-hide gate: suppress the window's draw while a full-screen game menu is open
        // (AutoHideBehindGameMenus) or before login (HideUntilInWorld). The renderer deactivates the root and
        // skips the value pull when hidden — the perf win the IMGUI path got via WindowGatingPolicy. Recomputed
        // each apply (~10 Hz), so it responds within ≤100 ms of a menu opening/closing.
        var hide = WindowGatingPolicy.IsDrawSuppressed(e.Reg.Spec, _menuState.IsFullScreenMenuOpen, _clientState.IsLoggedIn);
        PerfProbe.BeginWindow(e.Reg.Spec.Id);
        try { _renderer.ApplyValues(e.Token, e.Reg, hide); }
        catch (Exception ex) { _log.Warning($"[Window/{e.Reg.Spec.Id}] apply: {ex.Message}"); }
        finally { PerfProbe.EndWindow(e.Reg.Spec.Id); }
    }
    private void DestroyIfMounted(Entry e) { if (e.Token is null) return; _renderer.Destroy(e.Token); e.Token = null; }

    private void PruneRemoved()
    {
        List<string>? gone = null;
        foreach (var e in _windows.Values) if (e.Removed) { DestroyIfMounted(e); (gone ??= new()).Add(e.Reg.Spec.Id); }
        if (gone != null) foreach (var id in gone) _windows.Remove(id);
    }

    public void DisposeAll() { foreach (var e in _windows.Values) DestroyIfMounted(e); _windows.Clear(); }

    private sealed class Entry : IWindowControl
    {
        public Entry(WindowRegistration reg) { Reg = reg; }
        public WindowRegistration Reg { get; }
        public WindowService Owner = null!;
        public object? Token; public bool Visible; public bool Dirty = true; public bool Removed;
        public WindowRect LastRect, LastSavedRect;   // drag/resize-persist tracking (WindowService.Layout)
        public Entry Init(bool startVisible) { Visible = startVisible; return this; }
        public bool IsShown => Token != null && Visible;
        public void MarkDirty() => Dirty = true;
        public void SetVisible(bool visible) => Visible = visible;
        // Persist via the owner's slot-aware path (same one the layout-editor eye-toggle uses), so a plugin
        // hotkey / close button records to the active layout slot and the choice survives relaunch.
        public void SetVisiblePersist(bool visible) => Owner.SetVisiblePersist(Reg.Spec.Id, visible);
        public void Remove() => Removed = true;

        public WindowRect Rect => Token != null ? Owner._renderer.GetRect(Token) : Reg.Spec.DefaultRect;
        public void SetRect(WindowRect rect)
        {
            if (Token == null) return;
            Owner._renderer.SetRect(Token, rect);
            // Persist the explicit set immediately (the per-mode geometry restore path) — don't wait for a drag.
            if (Owner._storage != null && Owner._resolution != null)
            {
                Owner._storage.Save(Owner._storage.ActiveSlot, Reg.Spec.Id, Owner._resolution(), rect, Visible);
                LastRect = LastSavedRect = rect;
            }
        }
    }
    private sealed class NoOpHandle : IWindowControl
    { public bool IsShown => false; public void MarkDirty(){} public void SetVisible(bool v){} public void SetVisiblePersist(bool v){} public void Remove(){}
      public WindowRect Rect => default; public void SetRect(WindowRect rect){} }
}
