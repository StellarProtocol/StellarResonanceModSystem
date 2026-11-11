using System;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// "Hide others" (SHOW group chip, default ON): while a filter other than All windows is picked, everything the filter
/// leaves out is TEMPORARILY hidden so the user arranges the chosen set on a clean screen —
/// <list type="bullet">
/// <item>"Game UI only" → every edit-mode mod window is hidden;</item>
/// <item>"&lt;plugin&gt;" → every edit-mode mod window of another plugin AND all allowlist game-UI elements are hidden.</item>
/// </list>
/// Transient by construction: windows use <c>WindowService.SyncEditHidden</c> (draw-suppress, never Visible / never
/// saved), game UI uses <c>NativeUiService.SetTransientHidden</c> (wrapper scale 0, never Visible / IsModified). The
/// desired state is re-applied every edit-mode tick (both calls are idempotent flag compares), so a filter change, chip
/// toggle, slot switch or scene rebuild is re-evaluated at once; edit-mode exit clears both. The chip state is an
/// in-memory editor preference for the session (the editor has no persisted prefs store; the filter isn't saved either).
/// </summary>
internal sealed partial class LayoutEditorOverlay
{
    private const float HideOthersLabelWidth = 112f;   // fits the longest locale ("Sembunyikan lain")

    private bool _hideOthers = true;
    private int _tempHiddenCount;
    private Func<string, bool>? _hiddenByFilter;   // cached delegate — no per-tick allocation

    private bool HideOthersActive => _hideOthers && FilterActive;

    // Per edit-mode tick, BEFORE input/outlines: push the desired transient-hide state to both services.
    private void SyncHideOthers()
    {
        var active = HideOthersActive;
        var windows = _windows?.SyncEditHidden(active ? (_hiddenByFilter ??= IsHiddenByFilter) : null) ?? 0;
        var native = _nativeUi?.SetTransientHidden(active && _editFilter == EditFilter.Plugin) ?? 0;
        _tempHiddenCount = windows + native;
    }

    // Edit-mode exit: undo everything (windows the user had shown come back; game UI returns to its own state).
    private void ClearHideOthers()
    {
        _windows?.SyncEditHidden(null);
        _nativeUi?.SetTransientHidden(false);
        _tempHiddenCount = 0;
    }

    // An edit-mode window the current filter doesn't show (the same rule the outline/select use).
    private bool IsHiddenByFilter(string windowId) => !IsWindowEditableNow(windowId);

    private void ToggleHideOthers()
    {
        if (!FilterActive) return;
        _hideOthers = !_hideOthers;
        SyncHideOthers();
    }

    // [Hide others] [On|Off chip] for the SHOW group. Greyed (recessed, non-interactive) under "All windows". Two
    // complete chips swapped by a Conditional at one fixed 64 px footprint (SDL "Clickable on/off chip").
    private HudElement[] HideOthersControls(StellarStyle s)
    {
        string OnOff() => _text!.T(_hideOthers ? "common.on" : "common.off");
        return new HudElement[]
        {
            new CellElement(new TextElement(() => _text!.T("layout.toolbar.hideOthers"),
                Color: () => FilterActive ? s.Text() : s.Muted(), NoWrap: true, FontSize: 12)
                { RichEmoji = true, Ellipsize = true }, Width: HideOthersLabelWidth),
            new CellElement(new ConditionalElement(() => FilterActive,
                s.Chip(OnOff, () => _hideOthers, ToggleHideOthers),
                s.DisabledChip(OnOff)), Width: StellarStyle.ChipWidth),
        };
    }

    // The toolbar's THIRD status line: "N hidden while editing" (separate from the status line's user-hidden "N hidden").
    // A non-breaking space when nothing is temp-hidden, so the reserved line keeps its height (no toolbar jump).
    private string TempHiddenLine()
        => _tempHiddenCount > 0 ? _text!.TFormat("layout.toolbar.tempHidden", _tempHiddenCount) : " ";
}
