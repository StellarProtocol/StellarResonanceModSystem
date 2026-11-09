using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// The colour picker's own child window (option B, user-approved 2026-11-09; content = ThemeEditorBody.DescribePicker).
//  • Opens when a row's Edit is clicked in "Edit colours"; EVERY fresh open (hidden → shown) DOCKS it beside the CURRENT
//    Edit colours window: its right edge + DockGap, top aligned with the clicked row; the left side when the right has
//    no room; clamped on screen. Its position is never persisted.
//  • Docked, it follows Edit colours each tick. Once the user drags the picker itself (its rect is no longer the one we
//    set) it is UNDOCKED until it closes — the house "stop re-placing at the first move you didn't make" rule.
//  • Edit on another row re-targets the SAME window: docked → realigned with the new row; undocked → stays put. Fronted
//    explicitly then (it is already visible, and Edit colours may have been activated since — Rule 5 exception). A
//    show from hidden lands on top by itself (non-sticky WindowStacking).
//  • Closes with: the row's Close pill, its own ✕, Edit colours closing (its ✕ / a resolved confirm), or the hub closing (CloseColours). Never
//    left orphaned: the tick closes it whenever no row is being edited or Edit colours is not open.
// IsolateCanvas: dragging the SV square / hue bar / opacity slider flips pixelPerfect on this canvas only.
internal sealed partial class ThemesPanel
{
    internal const string PickerWindowId = "stellar.settings.colourpicker";
    private const float GlassPad = 12f;                                             // GlassMenu body padding
    private const float PickerWidth = ThemeEditorBody.PickerContentWidth + 2f * GlassPad;   // 260
    private const float DockGap = 8f;
    private const float ScreenMargin = 8f;
    private const float RowTileHeight = 26f;   // the clicked row's tile (StellarStyle.ListTileHeight); the pill is centred in it
    // The first SetVisible(true) MOUNTS the window and applies its DefaultRect after our SetRect, so the docked rect is
    // re-asserted for a few ticks (the help popover's recipe); no undock detection during that burst.
    private const int DockBurstTicks = 4;

    private IWindowControl? _picker;
    private IFramework? _fw;
    private bool _pickerDocked;
    private bool _pickerDockLeft;
    private float _pickerDy;          // picker top − Edit colours top while docked (canvas units)
    private int _pickerSeq = -1;      // the editor's EditSeq last placed
    private int _dockBurst;
    private WindowRect _pickerSet;    // the rect last requested

    private void RegisterPickerWindow(IWindowHost windows, SettingsKit kit, Func<bool> gate)
    {
        _picker = windows.Register(new WindowRegistration(
            new WindowSpec(PickerWindowId, "Stellar Settings: Colour picker", new WindowRect(1150f, 220f, PickerWidth, 0f),
                WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            {
                ShouldRender = gate, Draggable = true, Closable = true, StartVisible = false, ShowTitleBar = false,
                IsolateCanvas = true,
                TitleProvider = () => _text.T("colours.picker.title"),
            },
            _editor.DescribePicker(kit, ClosePicker),
            OnClose: ClosePicker));
    }

    private void ClosePicker()
    {
        _editor.CloseExpanded();
        HidePicker();
    }

    private void HidePicker()
    {
        _pickerDocked = false;
        _dockBurst = 0;
        _pickerSeq = -1;
        _picker?.SetVisible(false);
    }

    // Colours tick: open / re-target / follow / close the picker.
    private void TickPicker()
    {
        if (_picker == null || _colours == null) return;
        if (!_coloursOpen || _editor.ExpandedSlotKey == null) { if (_picker.IsShown || _pickerSeq >= 0) HidePicker(); return; }

        var seq = _editor.EditSeq;
        if (seq != _pickerSeq)
        {
            var fresh = !_picker.IsShown;
            _pickerSeq = seq;
            if (fresh) { _pickerDocked = true; _picker.SetVisible(true); }
            else _picker.BringToFront();   // already visible: re-targeted in place (Rule 5 exception)
            if (_pickerDocked) Dock(fresh);
            _picker.MarkDirty();
            return;
        }
        if (!_pickerDocked || !_picker.IsShown) return;

        var actual = _picker.Rect;
        if (_dockBurst > 0) { _dockBurst--; _picker.SetRect(_pickerSet); return; }
        // Moved by the user (the rect is not where we put it) → undocked until it closes.
        if (MathF.Abs(actual.X - _pickerSet.X) > 1f || MathF.Abs(actual.Y - _pickerSet.Y) > 1f) { _pickerDocked = false; return; }
        var target = DockedRect(actual.Height);
        if (MathF.Abs(target.X - actual.X) > 0.5f || MathF.Abs(target.Y - actual.Y) > 0.5f) Place(target);
    }

    // Dock at the clicked row: the row's top (from the Edit pill's screen rect, centred in the 26 px tile) relative to
    // Edit colours' top; the side is chosen on every fresh open / re-target.
    private void Dock(bool fresh)
    {
        var c = _colours!.Rect;
        var k = CanvasPerScreenPx();
        var a = _editor.EditAnchor;
        var rowTop = (a.Y + a.Height / 2f) * k - RowTileHeight / 2f;
        _pickerDy = rowTop - c.Y;
        var canvasW = CanvasW();
        _pickerDockLeft = c.X + c.Width + DockGap + PickerWidth > canvasW - ScreenMargin;
        Place(DockedRect(_picker!.Rect.Height));
        if (fresh) _dockBurst = DockBurstTicks;
    }

    private WindowRect DockedRect(float pickerHeight)
    {
        var c = _colours!.Rect;
        var x = _pickerDockLeft ? c.X - DockGap - PickerWidth : c.X + c.Width + DockGap;
        var h = pickerHeight > 1f ? pickerHeight : 0f;
        x = MathF.Max(ScreenMargin, MathF.Min(x, CanvasW() - ScreenMargin - PickerWidth));
        var y = MathF.Max(ScreenMargin, MathF.Min(c.Y + _pickerDy, CanvasH() - ScreenMargin - h));
        return new WindowRect(x, y, PickerWidth, h);
    }

    private void Place(WindowRect r)
    {
        _pickerSet = r;
        _picker!.SetRect(r);
    }

    // OnClickWithRect reports SCREEN pixels; window rects are CANVAS units (WindowBuilder-Patterns.md, click-to-open).
    private float CanvasPerScreenPx()
        => _fw != null && _fw.ScreenWidth > 0 && _fw.CanvasWidth > 1 ? (float)_fw.CanvasWidth / _fw.ScreenWidth : 1f;

    private float CanvasW() => _fw != null && _fw.CanvasWidth > 1 ? _fw.CanvasWidth : 1920f;
    private float CanvasH() => _fw != null && _fw.CanvasHeight > 1 ? _fw.CanvasHeight : 1080f;
}
