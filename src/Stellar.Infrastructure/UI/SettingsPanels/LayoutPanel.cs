using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI.SettingsPanels;

/// <summary>
/// Settings → Layout panel. Combines slot management (rename / add / remove,
/// switch active), snap config (enabled toggle + threshold slider), and a
/// per-window inspector + reset buttons. Phase 8's floating top-center
/// toolbar still exists but is now superseded by this panel — only the
/// per-window outlines + drag input remain in LayoutEditorOverlay.
/// </summary>
internal sealed partial class LayoutPanel
{
    private readonly LayoutStorage _storage;
    private readonly LayoutEditorService _editor;
    private readonly ITheme _theme;
    private readonly ILocalization _loc;
    private string _renameBuffer = "";
    private const int MaxSlots = 8;

    // Live slot label (read each poll, never snapshotted): "Name *" for the active slot, else "Name".
    private string SlotLabel(int idx)
    {
        var names = _storage.SlotNames;
        if (idx >= names.Count) return "";
        return idx == _storage.ActiveSlot ? names[idx] + " *" : names[idx];
    }

    public LayoutPanel(LayoutStorage storage, LayoutEditorService editor, ITheme theme, ILocalization loc)
    {
        _storage = storage;
        _editor = editor;
        _theme = theme;
        _loc = loc;
        _storage.SlotsChanged += OnSlotsChanged;
        _renameBuffer = _storage.SlotNames.Count > 0 ? _storage.SlotNames[_storage.ActiveSlot] : "";
    }

    /// <summary>uGUI element-tree form of <see cref="DrawBody"/> (SP1 Settings migration). Slot picker +
    /// rename + snap toggle/slider + reset buttons, wired to the same LayoutStorage/editor/ui; built once.</summary>
    public HudElement Describe()
    {
        var items = new System.Collections.Generic.List<HudElement>
        {
            new TextElement(() => _loc.T("layout.slot"), Emphasis: true),
        };
        // Slot buttons: bounded at MaxSlots, each shown live (Conditional on idx < SlotCount) with its label
        // read LIVE from _storage each poll — NOT snapshotted at build (a captured `SlotNames` list left the
        // button stale after a rename, and add/remove never reflected). Active slot gets the accent + " *".
        var slotRow = new System.Collections.Generic.List<HudElement>();
        for (var i = 0; i < MaxSlots; i++)
        {
            var idx = i;
            slotRow.Add(new ConditionalElement(() => idx < _storage.SlotCount,
                new ButtonElement(() => SlotLabel(idx), () => SwitchSlot(idx), Active: () => idx == _storage.ActiveSlot)));
        }
        slotRow.Add(new ConditionalElement(() => _storage.SlotCount < MaxSlots,
            new ButtonElement(() => "+", () => _storage.AddSlot())));
        items.Add(new RowElement(slotRow));
        items.Add(new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("layout.rename")),
            // OnChange keeps _renameBuffer live as you type, so the Apply button (which reads it) works even
            // without pressing Enter first (InputElement.Submit only fires on Enter/blur).
            new InputElement(() => _renameBuffer, s => _renameBuffer = s, 150f, OnChange: s => _renameBuffer = s),
            new ButtonElement(() => _loc.T("common.apply"), () => _storage.RenameSlot(_storage.ActiveSlot, _renameBuffer)),
        }));
        items.Add(new ButtonElement(() => _loc.T("layout.removeSlot"),
            () => { if (_storage.SlotCount > 2) _storage.RemoveSlot(_storage.ActiveSlot); }, () => _storage.SlotCount > 2));
        items.Add(new TextElement(() => _loc.T("layout.snap"), Emphasis: true));
        items.Add(new RowElement(new HudElement[] { new ToggleElement(() => "", () => _storage.SnapEnabled, v => _storage.SnapEnabled = v), new TextElement(() => _loc.T("common.enable")) }));
        items.Add(new RowElement(new HudElement[]
        {
            new TextElement(() => _loc.T("layout.threshold")),
            new SliderElement(() => _storage.SnapThresholdPx, v => _storage.SnapThresholdPx = v, 0f, 24f, () => _storage.SnapEnabled),
            new TextElement(() => _loc.TFormat("layout.px", _storage.SnapThresholdPx)),
        }));
        items.Add(new TextElement(() => _loc.T("layout.windowControls"), Emphasis: true));
        items.Add(new ButtonElement(() => _loc.T("layout.resetSelected"), () => ResetSelected()));
        items.Add(new ButtonElement(() => _loc.T("layout.resetAllSlot"), () => ResetAllInSlot()));
        items.Add(new SeparatorElement());
        items.Add(new TextElement(() => _editor.IsEditing ? _loc.T("layout.editExit") : _loc.T("layout.editEnter"), () => _theme.Colors.TextMuted));
        return new ColumnElement(items.ToArray());
    }

    private void OnSlotsChanged()
    {
        var names = _storage.SlotNames;
        if (_storage.ActiveSlot >= 0 && _storage.ActiveSlot < names.Count)
            _renameBuffer = names[_storage.ActiveSlot];
    }

    private void SwitchSlot(int index)
    {
        if (index == _storage.ActiveSlot) return;
        _storage.SetActiveSlot(index);
        OnSlotsChanged();
    }

    private void ResetSelected()
    {
        // The legacy IMGUI windows this once reset are gone (uGUI HUDs/windows own their reset
        // paths; native-game-UI is managed via the Game UI panel) — nothing left to reset here.
    }

    private void ResetAllInSlot()
    {
        // No legacy IMGUI windows remain to reset in the active slot.
    }

}
