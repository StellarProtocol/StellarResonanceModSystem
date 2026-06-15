using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Phase 9c — the "+ Add colour override" picker for <see cref="ThemeEditorBody"/>.
/// The sparse main list shows only overridden slots; this picker is where the
/// full registry (every plugin's colours) lives, behind a button, so the editor
/// scales to many plugins. Collapsible per-owner sections + a name/owner filter
/// + multi-select; "Add selected" seeds an override per checked slot at its
/// current resolved colour (a visual no-op until edited).
/// </summary>
/// <remarks>
/// IMGUI control-count stability: filter text and owner expand/collapse both
/// change how many rows render. To keep the Layout and Repaint passes of a
/// single frame in agreement (a mismatch throws ExitGUIException — see
/// docs/imgui-root-causes.md), the filter actually used for row visibility
/// (<see cref="_appliedFilter"/>) and any owner-expand toggle are applied ONLY on
/// the <c>EventType.Layout</c> event. The live text field still updates
/// <c>_pickerFilter</c> immediately; the row set just reflows one frame later.
/// </remarks>
internal sealed partial class ThemeEditorBody
{
    private bool _pickerOpen;
    private string _pickerFilter = "";
    private string _appliedFilter = "";
    private readonly HashSet<string> _checkedSlots = new();

    private void TogglePicker()
    {
        _pickerOpen = !_pickerOpen;
        if (!_pickerOpen) _checkedSlots.Clear();   // discard pending selection on close
    }

    // Sandbox visual-harness seam (internal, not plugin-facing): force the
    // add-picker open/closed (and optionally pre-fill the filter) for a
    // deterministic headless capture. Batchmode OnGUI receives no mouse/keyboard
    // events, so the picker can't be opened or filtered by interacting.
    internal void SetPickerOpenForCapture(bool open, string filter = "")
    {
        _pickerOpen = open;
        _pickerFilter = filter;
        _appliedFilter = filter;
    }

    private void CommitSelected()
    {
        foreach (var key in _checkedSlots)
        {
            if (_overrides.HasOverride(key)) continue;          // belt-and-braces
            _overrides.SetOverride(key, _overrides.Resolve(key)); // seed at current resolved colour
        }
        _checkedSlots.Clear();
        _pickerOpen = false;
        _editDirty = true;   // FlushEditsOnRelease persists + rebuilds chrome textures
    }

    private void SetChecked(string key, bool on)
    {
        if (on) _checkedSlots.Add(key); else _checkedSlots.Remove(key);
    }

    // Addable = a plugin slot (system "Theme" colours always show in the main list,
    // never the picker), not already overridden, and matching the applied filter.
    private bool IsAddable(ColorSlotInfo s)
        => s.Owner != SystemOwner && !_overrides.HasOverride(s.Key) && MatchesFilter(s);

    private bool MatchesFilter(ColorSlotInfo s)
    {
        if (_appliedFilter.Length == 0) return true;
        return s.Label.IndexOf(_appliedFilter, StringComparison.OrdinalIgnoreCase) >= 0
            || s.Owner.IndexOf(_appliedFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
