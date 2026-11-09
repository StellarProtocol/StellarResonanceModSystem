using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// The toolbar's "Reset all" confirmation (approved prototype, the separate <c>dialog()</c>): a small house-style
/// window, NOT an inline swap inside the toolbar, so the toolbar itself never changes size. GlassMenu, no title bar,
/// 380 wide. Header = 32 px Danger "!" tile + bold "Reset every window?" / "Reset shown windows?" + muted "LAYOUT
/// EDITOR" + round ✕; body = one Tile box "{N} windows go back to their default size and place in slot {slot}. Hidden
/// ones are shown again." (N = the windows the current filter covers); footer = Danger "Reset all"/"Reset shown" +
/// Neutral "Cancel". Opens centred under the toolbar; closes on Cancel / ✕ / after the reset / on edit-mode exit.
/// </summary>
internal sealed partial class LayoutEditorOverlay
{
    private const float DialogWidth = 380f;
    private const float DialogHeightEstimate = 170f;   // only for the initial rect; the window hugs its content
    private const float DialogGapBelowToolbar = 8f;
    private const float CancelLabelWidth = 72f;
    // The dialog must draw above the toolbar's override canvas (32759) and the top input blocker (32760), below the
    // dropdown popup root canvas (32766) and the 32767 short cap (WindowBuilder-Patterns "sorting-layer map").
    private const int ResetDialogSortingOrder = 32761;
    // Re-centre under the toolbar for this many ticks after opening: a SetRect before the window is built is ignored,
    // and the build restores the saved rect a few frames late (WindowBuilder-Patterns "Centring a window on EVERY open").
    private const int DialogCentreTicks = 30;

    private IWindowControl? _resetDialog;
    private int _dialogCentreTicks;

    private void OpenResetDialog()
    {
        EnsureResetDialogRegistered();
        if (_resetDialog == null) return;
        _resetDialog.SetVisible(true);
        _dialogCentreTicks = DialogCentreTicks;
        CentreResetDialog();
    }

    private void CloseResetDialog()
    {
        _dialogCentreTicks = 0;
        _resetDialog?.SetVisible(false);
    }

    // Pumped from TickInput while editing: keep the freshly-opened dialog centred under the toolbar's CURRENT rect
    // (the toolbar is draggable) until the build has settled.
    private void TickResetDialog()
    {
        if (_dialogCentreTicks <= 0) return;
        _dialogCentreTicks--;
        CentreResetDialog();
    }

    private void CentreResetDialog()
    {
        if (_resetDialog == null || _toolbarWindow == null) return;
        var tb = _toolbarWindow.Rect;   // canvas units, same domain as SetRect
        var h = _resetDialog.Rect.Height > 0f ? _resetDialog.Rect.Height : DialogHeightEstimate;
        _resetDialog.SetRect(new WindowRect(tb.X + (tb.Width - DialogWidth) / 2f, tb.Y + tb.Height + DialogGapBelowToolbar,
            DialogWidth, h));
    }

    private void EnsureResetDialogRegistered()
    {
        if (_resetDialog != null || _windows == null || _s == null) return;
        var spec = new WindowSpec(Stellar.Infrastructure.Game.WindowBuilder.LayoutResetDialogWindowId, "",
            new WindowRect(0f, 120f, DialogWidth, DialogHeightEstimate), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
        {
            // Lives only inside edit mode (TickInput closes it on exit); hidden over the loading screen like the toolbar.
            ShouldRender = () => _editor.IsEditing && (_clientState.UiState & GameUIState.Loading) == 0,
            StartVisible = false,
            ShowTitleBar = false,
            Draggable = false,   // re-placed under the toolbar on every open; nothing to persist
            OverrideSortingOrder = ResetDialogSortingOrder,
            SlotIndependentLayout = true,
        };
        _resetDialog = _windows.Register(new WindowRegistration(spec, BuildResetDialogRoot()));
    }

    private HudElement BuildResetDialogRoot()
    {
        var s = _s!;
        const float inner = DialogWidth - 24f;   // the window's fixed 12 px padding each side
        var header = new RowElement(new HudElement[]
        {
            s.FilledTile(new TextElement(() => "!", Color: () => StellarStyle.White, Width: 32f, Align: TextAlign.Center,
                NoWrap: true, FontSize: 16) { Bold = true }, s.DangerFill, 32f, 7f),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _text!.T(FilterActive ? "layout.toolbar.confirm.shownQ" : "layout.toolbar.confirm.allQ"),
                    Color: () => s.Text(), NoWrap: true, FontSize: 14) { Bold = true, Ellipsize = true },
                new TextElement(() => _text!.T("layout.toolbar.heading").ToUpperInvariant(), Color: () => s.Muted(),
                    NoWrap: true, FontSize: 11) { Bold = true },
            }, Gap: 1f), Weight: 1f),
            s.CloseIconButton(CloseResetDialog, () => s.Background()),
        }, Gap: 10f);
        var body = new BubbleElement(new TextElement(
            () => _text!.TFormat("layout.toolbar.confirm.body", CountResetTargets(), _storage.GetSlotName(_storage.ActiveSlot)),
            Color: () => s.Text(), Width: inner - 20f))
        {
            FillColorFn = () => s.Fill(s.Tile()), CornerRadius = StellarStyle.TileRadius, PaddingX = 10f, PaddingY = 8f,
            MaxWidth = inner,
        };
        var footer = new RowElement(new HudElement[]
        {
            new SpacerElement(),   // flexible: right-aligns the pills
            s.DangerButton(ResetAllLabel, ConfirmResetFromDialog, ResetAllLabelWidth),
            s.NeutralButton(() => _text!.T("common.cancel"), CloseResetDialog, CancelLabelWidth),
        }, Gap: 6f);
        return new ColumnElement(new HudElement[] { header, body, footer }, Gap: 10f);
    }

    // The number of elements "Reset all" / "Reset shown" will touch = exactly the set the reset enumerates.
    private int CountResetTargets() => FilterActive ? FilteredResetIds().Count : AllResetIds().Count;

    private void ConfirmResetFromDialog()
    {
        var filtered = FilterActive;
        var name = FilterName();
        if (filtered) ResetFilteredWindows(); else ResetAllWindows();
        CloseResetDialog();
        Feedback(filtered ? _text!.TFormat("layout.toolbar.fb.resetShown", name) : _text!.T("layout.toolbar.fb.resetAll"));
    }
}
