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
    /// OnGUI, so poll legacy <see cref="Input"/> each frame while a cell is capturing. Host calls this per
    /// frame; cheap no-op unless capturing. Esc cancels; first non-modifier <c>GetKeyDown</c> commits.</summary>
    public void PollCaptureUgui()
    {
        if (_capturingActionId is not { } actionId) return;
        if (!Input.anyKeyDown) return;
        if (Input.GetKeyDown(KeyCode.Escape)) { CancelCapture(); return; }
        foreach (KeyCode k in System.Enum.GetValues(typeof(KeyCode)))
        {
            if (k == KeyCode.None || IsModifierKey(k) || !Input.GetKeyDown(k)) continue;
            var mods = ModifierKeys.None;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) mods |= ModifierKeys.Shift;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) mods |= ModifierKeys.Ctrl;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) mods |= ModifierKeys.Alt;
            var newBinding = new KeyBinding((StellarKeyCode)(int)k, mods);
            if (TryFindConflict(actionId, newBinding, out var conflictId)) _directory.Rebind(conflictId, null);
            _directory.Rebind(actionId, newBinding);
            CancelCapture();
            return;
        }
    }

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
