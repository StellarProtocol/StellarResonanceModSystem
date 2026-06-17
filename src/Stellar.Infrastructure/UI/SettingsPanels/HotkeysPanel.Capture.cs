using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Capture state machine for <see cref="HotkeysPanel"/>. Reads
/// <see cref="Event.current"/> for KeyDown events while a binding cell is
/// active; ignores modifier-only presses, treats Esc as cancel, and on the
/// first non-modifier press commits a new <see cref="KeyBinding"/> via
/// <see cref="IHotkeyDirectory.Rebind"/>.
/// </summary>
internal sealed partial class HotkeysPanel
{
    /// <summary>uGUI capture poll (SP1 Settings migration) — there is no <see cref="Event.current"/> outside
    /// OnGUI, so poll legacy <see cref="Input"/> while a cell is capturing. Host-ticked at the THROTTLED
    /// framework rate (InvokeRepeating @ UpdateRateHz, not per-frame), so it detects a new binding by diffing
    /// held-key state across ticks rather than the per-frame-only <c>GetKeyDown</c> (which was sampled too
    /// coarsely — most presses missed, the "spam the key to bind it" bug). Cheap no-op unless capturing. Esc
    /// cancels; the first freshly-held non-modifier key commits with the modifiers held at that moment.</summary>
    public void PollCaptureUgui()
    {
        // Draining a just-committed bind: keep dispatch suppressed (the directory stays in capture) until the
        // bound key is RELEASED, so the same keypress that set the binding doesn't also fire the freshly-bound
        // action the instant capture ends. Independent of tick ordering (HotkeyService.Tick gates on capture).
        if (_drainReleaseKey != KeyCode.None)
        {
            if (!Input.GetKey(_drainReleaseKey)) { _drainReleaseKey = KeyCode.None; _directory.EndCapture(); }
            return;
        }
        if (_capturingActionId is not { } actionId) { if (_capturePrimed) ResetCapturePoll(); return; }
        if (Input.GetKey(KeyCode.Escape)) { ResetCapturePoll(); CancelCapture(); return; }
        // Del / Backspace clears the binding (unbind) instead of assigning a key — the cell hints this.
        if (Input.GetKey(KeyCode.Delete) || Input.GetKey(KeyCode.Backspace)) { ResetCapturePoll(); Unbind(actionId); return; }

        // First poll of a capture: record what's already held so a key down BEFORE capture began doesn't
        // instantly bind. Subsequent polls diff against this baseline.
        if (!_capturePrimed)
        {
            foreach (var k in _capturableKeys) if (Input.GetKey(k)) _prevHeldKeys.Add(k);
            _capturePrimed = true;
            return;
        }

        KeyCode pressed = KeyCode.None;
        foreach (var k in _capturableKeys)
        {
            if (!Input.GetKey(k)) { _prevHeldKeys.Remove(k); continue; }
            if (pressed == KeyCode.None && _prevHeldKeys.Add(k)) pressed = k;   // held now, wasn't before → fresh press
        }
        if (pressed == KeyCode.None) return;

        var mods = ModifierKeys.None;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) mods |= ModifierKeys.Shift;
        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) mods |= ModifierKeys.Ctrl;
        if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) mods |= ModifierKeys.Alt;
        var newBinding = new KeyBinding((StellarKeyCode)(int)pressed, mods);
        if (TryFindConflict(actionId, newBinding, out var conflictId)) _directory.Rebind(conflictId, null);
        _directory.Rebind(actionId, newBinding);
        ResetCapturePoll();
        _capturingActionId = null;        // stop the UI capture — the cell now shows the new binding
        _drainReleaseKey = pressed;       // but keep dispatch gated (no EndCapture yet) until the key is released
    }

    // Non-modifier capturable keys, computed once (Enum.GetValues allocates — don't do it per poll).
    private static readonly KeyCode[] _capturableKeys = BuildCapturableKeys();
    private static KeyCode[] BuildCapturableKeys()
    {
        var list = new System.Collections.Generic.List<KeyCode>();
        foreach (KeyCode k in System.Enum.GetValues(typeof(KeyCode)))
            if (k != KeyCode.None && !IsModifierKey(k)) list.Add(k);
        return list.ToArray();
    }

    private readonly System.Collections.Generic.HashSet<KeyCode> _prevHeldKeys = new();
    private bool _capturePrimed;
    private KeyCode _drainReleaseKey = KeyCode.None;   // just-bound key being drained until released (see PollCaptureUgui)
    private void ResetCapturePoll() { _prevHeldKeys.Clear(); _capturePrimed = false; }

    private void TryCaptureKey(string actionId)
    {
        var current = Event.current;
        if (current is null || current.type != EventType.KeyDown) return;
        var key = current.keyCode;
        if (key == KeyCode.None) return;
        if (key == KeyCode.Escape) { CancelCapture(); current.Use(); return; }
        if (IsModifierKey(key)) return;   // wait for non-modifier

        var mods = ModifierKeys.None;
        if (current.shift)   mods |= ModifierKeys.Shift;
        if (current.control) mods |= ModifierKeys.Ctrl;
        if (current.alt)     mods |= ModifierKeys.Alt;

        // UnityEngine.KeyCode integer values match StellarKeyCode (StellarKeyCode.cs
        // explicitly mirrors them so Infrastructure can cast freely).
        var newBinding = new KeyBinding((StellarKeyCode)(int)key, mods);
        if (TryFindConflict(actionId, newBinding, out var conflictId))
        {
            // v1 Replace policy: silently unbind the previous owner; user can
            // re-bind it from its own row. Spec describes a toast; deferred to
            // a follow-up if the silent overwrite proves confusing.
            _directory.Rebind(conflictId, null);
        }
        _directory.Rebind(actionId, newBinding);
        CancelCapture();
        current.Use();
    }

    /// <summary>
    /// End the capture window and notify the directory so
    /// <see cref="HotkeyService.Tick"/> resumes dispatching action callbacks.
    /// Always paired — Esc, commit, mouse-cancel, and the toggle-off click on
    /// the originating cell all funnel through here.
    /// </summary>
    private void CancelCapture()
    {
        _capturingActionId = null;
        _directory.EndCapture();
    }

    private static bool IsModifierKey(KeyCode k)
        => k is KeyCode.LeftShift or KeyCode.RightShift or KeyCode.LeftControl
              or KeyCode.RightControl or KeyCode.LeftAlt or KeyCode.RightAlt
              or KeyCode.LeftCommand or KeyCode.RightCommand
              or KeyCode.LeftWindows or KeyCode.RightWindows;

    private bool TryFindConflict(string excludingActionId, KeyBinding candidate, out string conflictId)
    {
        foreach (var a in _directory.Actions)
        {
            if (a.Id == excludingActionId) continue;
            if (a.CurrentBinding is { } cb && cb.Key == candidate.Key && cb.Modifiers == candidate.Modifiers)
            {
                conflictId = a.Id;
                return true;
            }
        }
        conflictId = string.Empty;
        return false;
    }
}
