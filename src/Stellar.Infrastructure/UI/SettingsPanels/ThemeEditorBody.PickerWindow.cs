using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// The colour picker's own child window content (option B, user-approved 2026-11-09): a compact title strip — the slot
// being edited ("Danger · errors") + a round ✕ — over the shared HSV picker (swatch, SV square, hue bar, Opacity,
// Hex), unchanged. ThemesPanel.Picker.cs owns the window: it opens when a row's Edit is clicked, docks beside the
// "Edit colours" window at that row and follows it. This side only records WHICH row was clicked and WHERE.
internal sealed partial class ThemeEditorBody
{
    internal const float PickerContentWidth = 236f;   // the picker's 212 px hue bar + slack; fixed, so the window never grows

    /// <summary>The slot whose colour the picker edits (null = no row is being edited → the picker window is closed).</summary>
    internal string? ExpandedSlotKey => _expandedSlotKey;

    /// <summary>Screen-pixel rect (top-left origin) of the Edit pill last clicked to open / re-target the picker.</summary>
    internal WindowRect EditAnchor { get; private set; }

    /// <summary>Bumped on every Edit click that leaves a row being edited (open or re-target) — the window side places
    /// the picker once per bump.</summary>
    internal int EditSeq { get; private set; }

    /// <summary>Stops editing (the picker's ✕, the row's Close, closing Edit colours).</summary>
    internal void CloseExpanded() => _expandedSlotKey = null;

    // A row's Edit / Close pill: toggles that row (Close = same row), remembering the pill's rect for the docking.
    private void EditRow(string key, WindowRect screenRect)
    {
        ToggleExpandKey(key);
        if (_expandedSlotKey == null) return;
        EditAnchor = screenRect;
        EditSeq++;
    }

    public HudElement DescribePicker(SettingsKit kit, Action close)
    {
        var s = kit.S;
        var strip = new RowElement(new HudElement[]
        {
            new CellElement(new TextElement(() => _expandedSlotKey is { } k ? DisplayLabel(k) : "", Color: () => s.Text(),
                NoWrap: true, FontSize: 13) { Bold = true, Ellipsize = true }, Weight: 1f),
            s.CloseIconButton(close, () => s.Background()),
        }, Gap: 8f);
        return new CellElement(new ColumnElement(new HudElement[] { strip, BuildSharedPicker() }, Gap: 8f),
            Width: PickerContentWidth);
    }
}
