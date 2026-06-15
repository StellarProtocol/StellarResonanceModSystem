using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// uGUI (declarative) form of the custom-theme colour editor — sibling to the IMGUI <c>Draw</c> path,
/// wired to the SAME state + services. Built once; visibility/value all flow through the framework's
/// poll-diff (Conditional/List/Func), so the IMGUI count-stability deferral hack (Layout-vs-Repaint:
/// <c>_appliedFilter</c>/<c>_pendingOwnerToggle</c>/<c>_pendingRemoveKey</c>) is NOT needed here —
/// retained uGUI just SetActives rows (spec refactor 9c). The slot rows are an index-based bounded List
/// pulling <see cref="IThemeOverrides.Slots"/> live, so slots registered AFTER the window was built still
/// appear (up to <see cref="MaxSlots"/>). One shared HSV ColorPicker is bound to the expanded slot.
/// </summary>
internal sealed partial class ThemeEditorBody
{
    private const int MaxNames = 12;   // bounded custom-theme name buttons
    private const int MaxSlots = 64;   // bounded slot rows (system + plugin colours)

    /// <summary>Per-frame tick from the uGUI hub (Host TickOverlayServices). Coalesces ColorPicker-drag
    /// edits to a single persist+rebake on mouse-release — the uGUI analog of the IMGUI per-Draw flush.</summary>
    public void TickUgui() => FlushEditsOnRelease();

    public HudElement Describe()
    {
        var nameEntry = new RowElement(new HudElement[]
        {
            new TextElement(() => _nameMode == NameMode.New ? "New theme:" : "Rename:"),
            new InputElement(() => _nameBuffer, s => _nameBuffer = s ?? "", 160f),
            new ButtonElement(() => "OK", TryCommitName),
            new ButtonElement(() => "Cancel", CancelNameMode),
        });

        return new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Custom Themes", Emphasis: true),
            new ConditionalElement(() => _nameMode == NameMode.None, BuildSelector(), nameEntry),
            new ConditionalElement(() => _nameError != null, new TextElement(() => _nameError ?? "", () => _theme.Colors.Warning)),
            new ConditionalElement(() => _namedTheme.ActiveCustomName != null && _nameMode == NameMode.None, BuildToolbar()),
            new ConditionalElement(() => _namedTheme.ActiveCustomName != null, BuildEditorActive(), BuildReadOnly()),
        });
    }

    private HudElement BuildSelector()
    {
        var nameSlots = new HudElement[MaxNames];
        for (var i = 0; i < MaxNames; i++)
        {
            var idx = i;
            nameSlots[i] = new ButtonElement(() => NameAt(idx), () => SelectCustomAt(idx),
                Active: () => idx < _store.Names.Count && _namedTheme.ActiveCustomName == _store.Names[idx]);
        }
        return new ColumnElement(new HudElement[]
        {
            new ListElement(() => System.Math.Min(_store.Names.Count, MaxNames), nameSlots),
            new ButtonElement(() => "+ New", () => EnterNameMode(NameMode.New, "")),
            new ConditionalElement(() => _store.Names.Count == 0,
                new TextElement(() => "No custom themes yet — clone a preset to start.", () => _theme.Colors.TextMuted)),
        });
    }

    private HudElement BuildToolbar()
        => new RowElement(new HudElement[]
        {
            new ButtonElement(() => "Duplicate", () => EnterNameMode(NameMode.New, (_namedTheme.ActiveCustomName ?? "") + "_copy")),
            new ButtonElement(() => "Rename", BeginRename),
            new ButtonElement(() => _confirmDelete ? "Confirm?" : "Delete", DeleteUgui),
        });

    private HudElement BuildReadOnly()
        => new ColumnElement(new HudElement[]
        {
            new TextElement(() => "Built-in presets are read-only."),
            new ButtonElement(() => "Duplicate to customise", () => EnterNameMode(NameMode.New, _namedTheme.Active + "_custom")),
        });

    // No fixed-height Scroll wrapper: a fixed Scroll reserved 320 px even for a short override list, leaving
    // a big empty grey band at the window bottom. The list flows directly so the window content-sizes to its
    // actual height (the whole window scrolls via the game-window chrome if it ever exceeds the screen).
    private HudElement BuildEditorActive()
        => new ColumnElement(new HudElement[]
        {
            BuildOverrideList(),
            new ConditionalElement(() => _expandedSlotKey != null, BuildSharedPicker()),
            BuildAddSection(),
        });

    private HudElement BuildOverrideList()
    {
        var slots = new HudElement[MaxSlots];
        for (var i = 0; i < MaxSlots; i++)
        {
            var idx = i;
            var row = new RowElement(new HudElement[]
            {
                new SwatchElement(() => ColorAt(idx), 15f),
                new TextElement(() => LabelAt(idx)),
                new SpacerElement(),
                new TextElement(() => HexAt(idx), () => _theme.Colors.TextMuted),
                new ButtonElement(() => _expandedSlotKey == KeyAt(idx) && KeyAt(idx).Length > 0 ? "▾" : "Edit", () => ToggleExpandAt(idx)),
                new ButtonElement(() => IsSystemAt(idx) ? "Reset" : "Remove", () => RemoveAt(idx), Enabled: () => HasOverrideAt(idx)),
            });
            slots[i] = new ConditionalElement(() => ShowInMainList(idx), row);
        }
        return new ListElement(() => SlotRowCount(), slots);
    }

    private HudElement BuildAddSection()
    {
        var pickerSlots = new HudElement[MaxSlots];
        for (var i = 0; i < MaxSlots; i++)
        {
            var idx = i;
            var row = new RowElement(new HudElement[]
            {
                new ToggleElement(() => "", () => _checkedSlots.Contains(KeyAt(idx)), on => SetCheckedAt(idx, on)),
                new SwatchElement(() => ColorAt(idx), 15f),
                new TextElement(() => LabelAt(idx)),
                new SpacerElement(),
                new TextElement(() => HexAt(idx), () => _theme.Colors.TextMuted),
            });
            pickerSlots[i] = new ConditionalElement(() => ShowInPicker(idx), row);
        }
        var body = new ColumnElement(new HudElement[]
        {
            new RowElement(new HudElement[]
            {
                new TextElement(() => "Filter"),
                // Live as-you-type (OnChange) so the list reflows per keystroke — matches the IMGUI filter feel.
                new InputElement(() => _pickerFilter, s => _pickerFilter = s ?? "", 180f, OnChange: s => _pickerFilter = s ?? ""),
            }),
            new ListElement(() => SlotRowCount(), pickerSlots),
            new RowElement(new HudElement[]
            {
                new ButtonElement(() => _checkedSlots.Count > 0 ? $"Add selected ({_checkedSlots.Count})" : "Add selected",
                    CommitSelected, Enabled: () => _checkedSlots.Count > 0),
                new ButtonElement(() => "Cancel", TogglePicker),
            }),
        });
        return new ColumnElement(new HudElement[]
        {
            new ButtonElement(() => _pickerOpen ? "▾ Add colour override" : "+ Add colour override", TogglePicker),
            new ConditionalElement(() => _pickerOpen, body),
        });
    }

    // One shared HSV picker, bound to whatever slot is currently expanded (the binding re-syncs the SV
    // square + markers when _expandedSlotKey changes — see WindowBuilder.ColorPickerBinding).
    private HudElement BuildSharedPicker()
        => new ColorPickerElement(
            () => _expandedSlotKey is { } k ? _overrides.Resolve(k) : new ColorRgba(0f, 0f, 0f, 1f),
            c =>
            {
                if (_expandedSlotKey is not { } k) return;
                _overrides.SetOverride(k, c); _hexBuffer = ToHex(c); _editDirty = true;
                // Live recolour for chrome (Theme.*) slots so the window/HUD update in real time as you drag
                // (the in-place re-skin makes this flicker-free). The swatch/preview already track live via the
                // poll; persistence is still coalesced to mouse-release (FlushEditsOnRelease). Plugin colours
                // don't touch the chrome, so they skip the rebake.
                if (k.StartsWith(SystemOwner, System.StringComparison.Ordinal)) _namedTheme.NotifyColorsChanged();
            });

    // ---- index-based live accessors (slot set may grow after build; pull live, bounded by SlotRowCount) ----

    private int SlotRowCount() => System.Math.Min(_overrides.SlotCount, MaxSlots);

    // Cache the slot list — IThemeOverrides.Slots allocates a fresh List + a record per slot on every call,
    // and the editor's row Funcs hit SlotAt() dozens of times per poll. Refresh only when the registered
    // count changes (late plugin registration), so the steady-state poll allocates nothing here.
    private System.Collections.Generic.IReadOnlyList<ColorSlotInfo>? _slotCache;
    private int _slotCacheCount = -1;
    private ColorSlotInfo? SlotAt(int i)
    {
        if (_overrides.SlotCount != _slotCacheCount) { _slotCache = _overrides.Slots; _slotCacheCount = _overrides.SlotCount; }
        var list = _slotCache;
        return list != null && i >= 0 && i < list.Count ? list[i] : null;
    }
    private string KeyAt(int i) => SlotAt(i)?.Key ?? "";
    private string LabelAt(int i) => SlotAt(i) is { } s ? $"{s.Owner} · {s.Label}" : "";
    private string HexAt(int i) => KeyAt(i) is { Length: > 0 } k ? ToHex(_overrides.Resolve(k)) : "";
    private ColorRgba ColorAt(int i) => KeyAt(i) is { Length: > 0 } k ? _overrides.Resolve(k) : new ColorRgba(0f, 0f, 0f, 0f);
    private bool IsSystemAt(int i) => SlotAt(i)?.Owner == SystemOwner;
    private bool HasOverrideAt(int i) => KeyAt(i) is { Length: > 0 } k && _overrides.HasOverride(k);

    // Main list: system slots always show (Reset-to-default); plugin slots only when overridden (sparse).
    private bool ShowInMainList(int i) => SlotAt(i) is { } s && (s.Owner == SystemOwner || _overrides.HasOverride(s.Key));
    // Picker: addable plugin slots (not system, not already overridden) matching the live filter.
    private bool ShowInPicker(int i)
        => SlotAt(i) is { } s && s.Owner != SystemOwner && !_overrides.HasOverride(s.Key) && MatchesPickerFilter(s);

    private bool MatchesPickerFilter(ColorSlotInfo s)
    {
        var f = _pickerFilter ?? "";
        if (f.Length == 0) return true;
        return s.Label.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0
            || s.Owner.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ---- uGUI immediate handlers (no Layout deferral — retained uGUI applies at once) ----

    private string NameAt(int i) => i < _store.Names.Count ? _store.Names[i] : "";

    private void SelectCustomAt(int i)
    {
        if (i >= _store.Names.Count) return;
        var name = _store.Names[i];
        _namedTheme.SetActiveCustom(name, _store.BasePresetOf(name));
        _confirmDelete = false;
        _expandedSlotKey = null;
    }

    private void BeginRename()
    {
        var a = _namedTheme.ActiveCustomName;
        if (a == null) return;
        EnterNameMode(NameMode.Rename, a);
        _renameTarget = a;
    }

    private void DeleteUgui()
    {
        var active = _namedTheme.ActiveCustomName;
        if (active == null) return;
        if (_confirmDelete)
        {
            var b = _store.BasePresetOf(active);
            _store.Delete(active);
            _namedTheme.SetActive(b);
            _confirmDelete = false;
        }
        else _confirmDelete = true;
    }

    private void ToggleExpandAt(int i)
    {
        var k = KeyAt(i);
        if (k.Length == 0) return;
        ToggleExpand(k, _overrides.Resolve(k));
    }

    private void RemoveAt(int i)
    {
        var k = KeyAt(i);
        if (k.Length == 0 || !_overrides.HasOverride(k)) return;
        _overrides.ClearOverride(k);
        if (_expandedSlotKey == k) _expandedSlotKey = null;
        _overrides.Flush();
        _namedTheme.NotifyColorsChanged();
    }

    private void SetCheckedAt(int i, bool on)
    {
        var k = KeyAt(i);
        if (k.Length > 0) SetChecked(k, on);
    }
}
