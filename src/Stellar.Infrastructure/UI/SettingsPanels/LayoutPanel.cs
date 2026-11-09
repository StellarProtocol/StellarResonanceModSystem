using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Layout, two columns. Left: "Layout slots" — the slot list (5 rows reserved, then it scrolls): click a row
/// to switch, the active one on the accent wash with an "Active" chip; "Rename" opens an inline field + Save / ✕; ✕
/// removes (only while more than 2 slots exist) after a confirm; "+ Add slot" up to 8. Right: "Edit mode" (enter / exit
/// layout editing), "Snap" (switch + threshold), "Cursor" (sticky cursor). The old "Reset selected to default" /
/// "Reset all in this slot" buttons are gone: their bodies were empty (the layout editor's toolbar resets windows).
/// </summary>
internal sealed partial class LayoutPanel
{
    private const int MaxSlots = 8;
    private const int VisibleSlotRows = 5;
    internal const float ColumnWidth = (SettingsHub.MainWidth - 8f) / 2f;
    private const float EditPillLabel = 146f;   // a 170 px pill: fits "Enter / Exit layout editing" in every locale

    private readonly LayoutStorage _storage;
    private readonly LayoutEditorService _editor;
    private readonly ILocalization _loc;
    private SettingsKit _k = null!;
    private int _renaming = -1;         // slot whose inline rename field is open, -1 = none
    private string _renameBuffer = "";
    private int _renameFocus;           // bumped on every rename open → InputElement.FocusRequest
    private int _confirmRemove = -1;    // slot awaiting the remove confirmation, -1 = none
    private readonly StellarStyle.HoverFlag[] _rowHover = NewFlags();
    private readonly StellarStyle.HoverFlag[] _xHover = NewFlags();

    // theme is no longer read (the house kit carries the live colours); the parameter stays for the wiring.
    public LayoutPanel(LayoutStorage storage, LayoutEditorService editor, ITheme theme, ILocalization loc)
    {
        _storage = storage;
        _editor = editor;
        _loc = loc;
        // A slot removed / added elsewhere (layout toolbar) invalidates an open rename / confirm on a moved index.
        _storage.SlotsChanged += ResetTransient;
    }

    public HudElement Describe(SettingsKit kit)
    {
        _k = kit;
        kit.TabChanged += ResetTransient;
        // TopColumn: a Row centres a child vertically unless it is flexible, so each column ends in a filler that
        // stretches it to the tab height with its panels at the top (prototype flex columns).
        return new RowElement(new HudElement[]
        {
            new CellElement(StellarStyle.TopColumn(BuildSlotsPanel()), Width: ColumnWidth),
            new CellElement(StellarStyle.TopColumn(BuildEditPanel(), BuildSnapPanel(), BuildCursorPanel()), Width: ColumnWidth),
        }, Gap: 8f);
    }

    /// <summary>Header status: "Slot: Raid · Snap 8 px · Editing".</summary>
    public string Status(SettingsKit k)
    {
        var snap = _storage.SnapEnabled ? _loc.TFormat("layout.px", _storage.SnapThresholdPx) : _loc.T("common.off");
        var s = _loc.TFormat("layout.status", "<b>" + ActiveName() + "</b>", snap);
        return _editor.IsEditing ? s + " · " + k.Good(_loc.T("layout.status.editing")) : s;
    }

    private HudElement BuildSlotsPanel()
    {
        var s = _k.S;
        var rows = new HudElement[MaxSlots];
        for (var i = 0; i < MaxSlots; i++) rows[i] = SlotRow(i);
        var confirm = s.ConfirmPanel(new ConfirmSpec(
            () => _loc.TFormat("layout.remove.q", NameAt(_confirmRemove)),
            _k.T("layout.remove.consequence"),
            _k.T("common.remove"), ConfirmRemove,
            _k.T("common.cancel"), () => _confirmRemove = -1) { Width = ColumnWidth - 20f });
        var add = new RowElement(new HudElement[]
        {
            s.NeutralButton(_k.T("layout.addSlot"), AddSlot, enabled: () => _storage.SlotCount < MaxSlots),
        });
        return _k.Panel("layout.slots", () => _loc.TFormat("layout.slots.hint", _storage.SlotCount, MaxSlots),
            _k.Description("layout.slots.desc"),
            new ScrollElement(new ListElement(() => Math.Min(_storage.SlotCount, MaxSlots), rows),
                Height: StellarStyle.ListHeight(VisibleSlotRows)) { FixedHeight = true },
            new ConditionalElement(() => _confirmRemove >= 0 && _confirmRemove < _storage.SlotCount, confirm, add));
    }

    private HudElement BuildEditPanel()
    {
        var s = _k.S;
        return _k.Panel("layout.edit", () => "Shift+`",
            _k.Description("layout.edit.desc"),
            new RowElement(new HudElement[]
            {
                new ConditionalElement(() => _editor.IsEditing,
                    s.CautionButton(_k.T("layout.edit.exit"), ToggleEdit, EditPillLabel),
                    s.PrimaryButton(_k.T("layout.edit.enter"), ToggleEdit, EditPillLabel)),
            }));
    }

    private HudElement BuildSnapPanel()
        => _k.Panel("layout.snap", () => _storage.SnapEnabled ? _loc.T("layout.snap.hintOn") : _loc.T("common.off"),
            SettingsKit.Row(_k.Label(_k.T("layout.snap.toggle")),
                SettingsKit.Toggle(() => _storage.SnapEnabled, v => _storage.SnapEnabled = v),
                _k.Help("layout.snap", "layout.snap.toggle", "help.layout.snap", "tab.layout")),
            SettingsKit.Row(
                new TextElement(_k.T("layout.threshold"), Color: () => _k.S.Text(), Width: 80f, NoWrap: true) { Ellipsize = true },
                new CellElement(SettingsKit.Slider(() => _storage.SnapThresholdPx, v => _storage.SnapThresholdPx = MathF.Round(v),
                    0f, 24f, () => _storage.SnapEnabled), Weight: 1f),
                _k.Value(() => _loc.TFormat("layout.px", _storage.SnapThresholdPx), 44f),
                _k.Help("layout.threshold", "layout.threshold.title", "help.layout.threshold", "tab.layout")));

    private HudElement BuildCursorPanel()
        => _k.Panel("layout.cursor", null,
            SettingsKit.Row(_k.Label(_k.T("layout.stickyCursor")),
                SettingsKit.Toggle(() => _storage.StickyCursorEnabled, v => _storage.StickyCursorEnabled = v),
                _k.Help("layout.sticky", "layout.sticky.title", "help.layout.sticky", "tab.layout")));

    private void ToggleEdit()
    {
        _editor.ToggleEditMode();
        _k.Done(_loc.T(_editor.IsEditing ? "layout.fb.editOn" : "layout.fb.editOff"));
    }

    private string NameAt(int idx)
    {
        var names = _storage.SlotNames;
        return idx >= 0 && idx < names.Count ? names[idx] : "";
    }

    private string ActiveName() => NameAt(_storage.ActiveSlot);

    private void ResetTransient()
    {
        _renaming = -1;
        _confirmRemove = -1;
    }

    private static StellarStyle.HoverFlag[] NewFlags()
    {
        var a = new StellarStyle.HoverFlag[MaxSlots];
        for (var i = 0; i < a.Length; i++) a[i] = new StellarStyle.HoverFlag();
        return a;
    }
}
