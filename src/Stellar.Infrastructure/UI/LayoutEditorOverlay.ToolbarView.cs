using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// The layout toolbar's house-style VIEW (approved prototype <c>_scratch/prototypes/layout-toolbar.html</c>, option A
/// "one row" + the separate Reset-all confirm dialog): ToolHeader [28 px accent icon tile · bold "Layout Editor" · live
/// status "Slot X · N windows · N hidden", which shows "✓ …" click feedback for 3 s], then three Panel groups 40 px tall
/// — SHOW (filter dropdown), SLOT (slot dropdown), WINDOW (selection dropdown + Hide/Show + Reset) — a separator,
/// Reset all ("Reset shown" while a filter is active) and the Primary Done. One fixed width that never changes (R10):
/// every pill has a fixed label box sized for the longest locale. Behaviour lives in <c>LayoutEditorOverlay.Toolbar.cs</c>.
/// </summary>
internal sealed partial class LayoutEditorOverlay
{
    // Fixed geometry (canvas units). The prototype's English widths are 150 / 130 / 220 dropdowns and 64 / 64 / 118 /
    // 64 pills; the pills' LABEL boxes here fit the longest locale instead (id/fil/ja labels run longer — R10), which
    // makes the bar ~1,460 wide instead of ~1,320.
    internal const float ToolbarWidth = 1460f;
    private const float GroupHeight = 40f;
    private const float FilterDropWidth = 150f;
    private const float SlotDropWidth = 130f;
    private const float WindowDropWidth = 220f;
    private const float VisLabelWidth = 64f;        // Hide / Show            → 88 px pill
    private const float ResetLabelWidth = 52f;      // Reset (selected)       → 76 px pill
    internal const float ResetAllLabelWidth = 128f; // Reset all / Reset shown → 152 px pill
    private const float DoneLabelWidth = 60f;       // Done                   → 84 px pill
    private const float StatusWidth = 200f;
    private const float HeaderIconSize = 28f;

    private StellarStyle? _s;
    private StellarFeedbackLine? _feedback;   // only its 3 s timer: the toolbar shows the message in its status line

    /// <summary>Bind the framework house-style kit (live theme + window opacity). Wiring.Settings calls it before
    /// <see cref="SetWindows"/>, so it is set before the toolbar can register (pre-warm or first edit-enter).</summary>
    public void SetStyle(StellarStyle style)
    {
        _s = style;
        _feedback = new StellarFeedbackLine(style, () => "");
    }

    /// <summary>"✓ message" in the toolbar status for 3 s (R13: feedback where the player clicked).</summary>
    private void ToolbarDone(string message) => _feedback?.Show(message);

    private HudElement BuildToolbarRoot()
    {
        var s = _s!;
        var row = new RowElement(new HudElement[]
        {
            new SpacerElement(),   // flexible: centres the fixed content block in the fixed-width glass panel
            ToolbarHeader(s),
            Group(s, "layout.toolbar.capShow", new DropdownElement(FilterSelectedIndex, FilterOptions, OnFilterSelected,
                Width: FilterDropWidth) { Style = DropdownStyle.Pill, CornerRadius = 13f, MaxVisibleRows = 10 }),
            // Slots switch only (add/remove/rename stay in Settings › Layout). SlotNames is a live snapshot.
            Group(s, "layout.slot", new DropdownElement(() => _storage.ActiveSlot, () => _storage.SlotNames, SwitchToSlot,
                Width: SlotDropWidth) { Style = DropdownStyle.Pill, CornerRadius = 13f, MaxVisibleRows = 8 }),
            Group(s, "layout.toolbar.capWindow",
                new DropdownElement(SelectionSelectedIndex, SelectionOptions, OnSelectionPicked, Width: WindowDropWidth)
                    { Style = DropdownStyle.Pill, CornerRadius = 13f, MaxVisibleRows = 12 },
                // Hide/Show + Reset act on the selection; with nothing selected they are DISABLED (a sunken fill — the
                // house rule: pressable and disabled never look alike).
                s.NeutralButton(() => _text!.T(SelectedVisibility().Visible ? "layout.toolbar.hide" : "layout.toolbar.show"),
                    ToggleSelectedVisibility, VisLabelWidth, () => { var v = SelectedVisibility(); return v.Found && v.CanHide; }),
                s.NeutralButton(() => _text!.T("layout.toolbar.reset"), ResetSelected, ResetLabelWidth,
                    () => _editor.SelectedWindowId != null)),
            new SeparatorElement(Vertical: true),
            // "Reset all" while every window is shown, "Reset shown" under a filter — fixed width, so nothing moves.
            s.NeutralButton(ResetAllLabel, OpenResetDialog, ResetAllLabelWidth),
            s.PrimaryButton(() => _text!.T("common.done"), () => _editor.ToggleEditMode(), DoneLabelWidth),
            new SpacerElement(),
        }, Gap: 6f);
        // Column wrap so the Row gets the full panel width (childForceExpandWidth) → the flex spacers can centre.
        return new ColumnElement(new HudElement[] { row });
    }

    // [28 px accent tile with the layout glyph] [bold "Layout Editor" over the live status / feedback line].
    private HudElement ToolbarHeader(StellarStyle s) => new RowElement(new HudElement[]
    {
        new SpacerElement(2f, GroupHeight),
        s.IconTile("layout", HeaderIconSize),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => _text!.T("layout.toolbar.heading"), Color: () => s.Text(), NoWrap: true, FontSize: 14)
                { Bold = true },
            // Regular 12 px = the TMP face (RichEmoji honours FontSize; plain window text is a fixed 14 — SDL §6). "✓" is
            // plain text: bold ✓ is untrusted.
            new TextElement(ToolbarStatus, Color: () => _feedback?.IsShowing == true
                    ? s.Readable(s.Success(), s.Background()) : s.Muted(),
                NoWrap: true, FontSize: 12) { RichEmoji = true, Ellipsize = true },
        }, Gap: 1f), Width: StatusWidth),
    }, Gap: 8f);

    // A toolbar group (prototype .grp): a Panel bubble 40 px tall, radius 6 — muted bold UPPERCASE caption, then its
    // controls (Gap 6). Inset 8 left / 6 right, carried by fixed spacers inside a zero-padding bubble.
    private HudElement Group(StellarStyle s, string captionKey, params HudElement[] controls)
    {
        var items = new List<HudElement>(controls.Length + 3)
        {
            new SpacerElement(2f, GroupHeight),   // + the 6 gap = the 8 px left inset; also the 40 px height strut
            s.Caption(() => _text!.T(captionKey)),
        };
        items.AddRange(controls);
        items.Add(new SpacerElement(0.01f, 1f));   // + the 6 gap = the 6 px right inset
        return new BubbleElement(new RowElement(items, Gap: 6f))
        {
            FillColorFn = () => s.Fill(s.Panel()), CornerRadius = StellarStyle.TileRadius, PaddingX = 0f, PaddingY = 0f,
        };
    }

    // "Slot Raid · 9 windows · 2 hidden" (counts = the windows the current filter shows, hidden included), or the
    // "✓ …" feedback for 3 s after an action.
    private string ToolbarStatus()
    {
        if (_feedback is { IsShowing: true }) return "✓ " + _feedbackText;
        int n = 0, hidden = 0;
        foreach (var o in SelectableObjects()) { n++; if (!o.Visible) hidden++; }
        var slot = _storage.GetSlotName(_storage.ActiveSlot);
        return hidden > 0 ? _text!.TFormat("layout.toolbar.statusHidden", slot, n, hidden)
                          : _text!.TFormat("layout.toolbar.status", slot, n);
    }

    private string _feedbackText = "";

    private void Feedback(string message)
    {
        _feedbackText = message;
        ToolbarDone(message);
    }

    private bool FilterActive => _editFilter != EditFilter.All;

    private string ResetAllLabel() => _text!.T(FilterActive ? "layout.toolbar.resetShown" : "layout.toolbar.resetAll");

    // The current filter's caption ("Game UI", a plugin name) for feedback / the confirm text.
    private string FilterName()
    {
        var opts = FilterOptions();
        var i = FilterSelectedIndex();
        return i >= 0 && i < opts.Count ? opts[i] : "";
    }

    // Display name of an element id (native entries by their localized name; mod windows by their id, as listed).
    private string ElementName(string id)
    {
        foreach (var o in SelectableObjects()) if (o.Id == id) return o.Label;
        return id;
    }

    private void ToggleSelectedVisibility()
    {
        if (_editor.SelectedWindowId is not { } sel) return;
        var wasVisible = SelectedVisibility().Visible;
        ToggleVisibility(sel);
        Feedback(_text!.TFormat(wasVisible ? "layout.toolbar.fb.hidden" : "layout.toolbar.fb.shown", ElementName(sel)));
    }

    private void ResetSelected()
    {
        if (_editor.SelectedWindowId is not { } sel) return;
        ResetWindow(sel);
        Feedback(_text!.TFormat("layout.toolbar.fb.reset", ElementName(sel)));
    }
}
