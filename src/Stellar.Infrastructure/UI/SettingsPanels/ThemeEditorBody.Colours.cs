using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Infrastructure.UI.SettingsPanels;

// The house-style "Edit colours" child window (Stellar Settings: Edit colours): a ToolHeader (theme name, "Based on X ·
// N colours changed"), "Base" (window background, text, muted text, accent, border), "Status" (success, danger,
// caution), the shared HSV picker for the row being edited, "Other colours" (the remaining framework colours + every
// plugin colour this theme overrides, and the "+ Add colour override" picker), then Done. A row = swatch, name, hex,
// Edit (opens the picker), ✕ (back to the theme default; only while overridden).
internal sealed partial class ThemeEditorBody
{
    private const float HexWidth = 72f;
    private const float EditPillLabel = 44f;
    private const float PickerListHeight = 140f;

    // Display names for the Base / Status rows (the registry labels stay for "Other colours" and plugins).
    private static readonly Dictionary<string, string> NamedRowKeys = new(StringComparer.Ordinal)
    {
        ["Theme.MenuBackground"] = "colours.slot.background",
        ["Theme.MenuText"]       = "colours.slot.text",
        ["Theme.MenuMuted"]      = "colours.slot.muted",
        ["Theme.MenuAccent"]     = "colours.slot.accent",
        ["Theme.MenuBorder"]     = "colours.slot.border",
        ["Theme.Success"]        = "colours.slot.success",
        ["Theme.Danger"]         = "colours.slot.danger",
        ["Theme.Caution"]        = "colours.slot.caution",
    };

    private SettingsKit _k = null!;
    // Slot indexes of the "Other colours" rows and of the add-picker rows, refreshed per apply by their list's
    // Conditional When — a hidden row must not keep its list slot (each slot adds the list's 2 px gap).
    private readonly List<int> _otherIdx = new();
    private readonly List<int> _pickIdx = new();

    public HudElement DescribeColours(SettingsKit kit, Action close)
    {
        _k = kit;
        var s = kit.S;
        return new ColumnElement(new HudElement[]
        {
            ColoursHeader(),
            kit.Panel("colours.base", kit.T("colours.base.hint"), NamedRows(BaseKeys)),
            kit.Panel("colours.status", kit.T("colours.status.hint"), NamedRows(StatusKeys)),
            new ConditionalElement(() => _expandedSlotKey != null,
                s.SectionPanel(() => _expandedSlotKey is { } k ? DisplayLabel(k) : "", null, BuildSharedPicker())),
            kit.Panel("colours.other", kit.T("colours.other.hint"), OtherList(), BuildAddSection()),
            new RowElement(new HudElement[]
            {
                new SpacerElement(),
                s.PrimaryButton(kit.T("common.done"), close, StellarStyle.PillPairWidth - 24f),
            }),
        }, Gap: 8f);
    }

    private HudElement ColoursHeader()
    {
        var s = _k.S;
        return new RowElement(new HudElement[]
        {
            s.IconTile("theme", StellarStyle.ToolIconSize),
            new CellElement(new ColumnElement(new HudElement[]
            {
                new TextElement(() => _namedTheme.ActiveCustomName ?? "", Color: () => s.Text(), NoWrap: true, FontSize: 17)
                    { Bold = true, Ellipsize = true },
                new TextElement(() => _text.TFormat(ChangedCount() == 1 ? "colours.basedOn.one" : "colours.basedOn.many",
                        _text.T(ThemesPanel.PresetNameKey(BaseFromActive())), ChangedCount()),
                    Color: () => s.Muted(), NoWrap: true) { Ellipsize = true },
            }, Gap: 2f), Weight: 1f),
        }, Gap: 10f);
    }

    private HudElement[] NamedRows(string[] keys)
    {
        var rows = new HudElement[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            rows[i] = ColourRow(() => key, () => DisplayLabel(key));
        }
        return new HudElement[] { new ColumnElement(rows, Gap: 2f) };
    }

    private HudElement OtherList()
    {
        var slots = new HudElement[MaxSlots];
        for (var i = 0; i < MaxSlots; i++)
        {
            var idx = i;
            string Key() => idx < _otherIdx.Count ? KeyAt(_otherIdx[idx]) : "";
            slots[i] = ColourRow(Key, () => idx < _otherIdx.Count ? LabelAt(_otherIdx[idx]) : "");
        }
        return new ConditionalElement(() => { Refresh(_otherIdx, ShowInOtherList); return true; },
            new ListElement(() => _otherIdx.Count, slots));
    }

    private void Refresh(List<int> into, Func<int, bool> show)
    {
        into.Clear();
        for (var i = 0; i < SlotRowCount(); i++) if (show(i)) into.Add(i);
    }

    // One colour row: swatch, name, hex, Edit (Close while its picker is open), ✕ reset (only while overridden).
    private HudElement ColourRow(Func<string> key, Func<string> label)
    {
        var s = _k.S;
        bool Open() => key().Length > 0 && _expandedSlotKey == key();
        var hover = new StellarStyle.HoverFlag();
        return s.ListTile(StellarStyle.TileRow(
                new SwatchElement(() => ColorOf(key()), 18f),
                _k.LineLabel(label),
                new TextElement(() => HexOf(key()), Color: () => s.Muted(), Width: HexWidth, NoWrap: true),
                s.SmallPill(() => _text.T(Open() ? "common.close" : "common.edit"), PillKind.Neutral, () => ToggleExpandKey(key()),
                    EditPillLabel),
                new ConditionalElement(() => HasOverride(key()),
                    s.SmallCloseButton(() => ResetKey(key()), hover: hover), SettingsKit.HelpGap())),
            selected: Open);
    }

    private string DisplayLabel(string key)
    {
        if (NamedRowKeys.TryGetValue(key, out var k)) return _text.T(k);
        for (var i = 0; i < SlotRowCount(); i++) if (SlotAt(i) is { } s && s.Key == key) return LabelAt(i);
        return key;
    }

    // One shared HSV picker, bound to whatever slot is currently expanded (the binding re-syncs the SV square + markers
    // when _expandedSlotKey changes — see WindowBuilder.ColorPickerBinding).
    private HudElement BuildSharedPicker()
        => new ColorPickerElement(
            () => _expandedSlotKey is { } k ? _overrides.Resolve(k) : new ColorRgba(0f, 0f, 0f, 1f),
            c =>
            {
                if (_expandedSlotKey is not { } k) return;
                _overrides.SetOverride(k, c); _hexBuffer = ToHex(c); _editDirty = true;
                // Live recolour for chrome (Theme.*) slots so the window/HUD update in real time as you drag
                // (the in-place re-skin makes this flicker-free). Persistence is still coalesced to mouse-release
                // (FlushEditsOnRelease). Plugin colours don't touch the chrome, so they skip the rebake.
                if (k.StartsWith(SystemOwner, StringComparison.Ordinal)) _namedTheme.NotifyColorsChanged();
            });

    // "+ Add colour override" → filter field, the addable plugin colours (tick several), Add selected (N) / Cancel.
    private HudElement BuildAddSection()
    {
        var s = _k.S;
        var slots = new HudElement[MaxSlots];
        for (var i = 0; i < MaxSlots; i++)
        {
            var idx = i;
            int Slot() => idx < _pickIdx.Count ? _pickIdx[idx] : -1;
            string Key() => Slot() >= 0 ? KeyAt(Slot()) : "";
            slots[i] = s.ListTile(StellarStyle.TileRow(
                SettingsKit.Toggle(() => _checkedSlots.Contains(Key()), on => { if (Slot() >= 0) SetCheckedAt(Slot(), on); }),
                new SwatchElement(() => ColorOf(Key()), 18f),
                _k.LineLabel(() => Slot() >= 0 ? LabelAt(Slot()) : ""),
                new TextElement(() => HexOf(Key()), Color: () => s.Muted(), Width: HexWidth, NoWrap: true)));
        }
        var body = new ColumnElement(new HudElement[]
        {
            SettingsKit.Row(
                new TextElement(_k.T("themes.filter"), Color: () => s.Text(), NoWrap: true),
                new CellElement(new InputElement(() => _pickerFilter, v => _pickerFilter = v ?? "", 180f,
                    OnChange: v => _pickerFilter = v ?? "") { SingleLine = true }, Weight: 1f)),
            new ConditionalElement(() => { Refresh(_pickIdx, ShowInPicker); return true; },
                new ScrollElement(new ListElement(() => _pickIdx.Count, slots), Height: PickerListHeight) { FixedHeight = true }),
            new RowElement(new HudElement[]
            {
                s.PrimaryButton(() => _checkedSlots.Count > 0 ? _text.TFormat("themes.addSelectedN", _checkedSlots.Count)
                    : _text.T("themes.addSelected"), CommitSelected, 150f, enabled: () => _checkedSlots.Count > 0),
                s.NeutralButton(_k.T("common.cancel"), TogglePicker),
            }, Gap: 6f),
        }, Gap: 6f);
        return new ConditionalElement(() => _pickerOpen, body,
            new RowElement(new HudElement[] { s.NeutralButton(_k.T("themes.addOverride.closed"), TogglePicker) }));
    }
}
