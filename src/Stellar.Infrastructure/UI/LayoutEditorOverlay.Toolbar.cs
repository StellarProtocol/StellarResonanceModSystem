using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Toolbar half of <see cref="LayoutEditorOverlay"/> — migrated to a native uGUI Borderless window
/// (Stage C of the layout-editor uGUI migration). The bar is a single Row of framework elements
/// (heading, filter chips, slot picker, selection readout, reset buttons, hint), registered on the
/// framework <see cref="IWindowHost"/> and shown/hidden on the edit-mode edge. Behaviour (filter,
/// slot switch, resets) is unchanged — only the rendering moved off IMGUI.
/// </summary>
internal sealed partial class LayoutEditorOverlay
{
    private const int MaxSlots = 8;   // slot buttons built into the toolbar; extra collapse via Conditional

    private WindowService? _windows;        // bound late via SetWindows (concrete: editor enumerates its overlay windows)
    private IWindowControl? _toolbarWindow; // the uGUI toolbar window; registered lazily on first edit-enter

    // Edit-mode element-type filter (Phase 9a). All = Stellar windows + game UI; Stellar-only / GameUi-only
    // suppress the other category's outlines + click hit-tests. Read by ShouldOutlineStellar / ShouldOutlineGameUi.
    private enum EditFilter { All = 0, StellarOnly = 1, GameUiOnly = 2 }
    private EditFilter _editFilter = EditFilter.All;
    private bool ShouldOutlineStellar => _editFilter != EditFilter.GameUiOnly;
    private bool ShouldOutlineGameUi  => _editFilter != EditFilter.StellarOnly;

    /// <summary>Bind the framework window service so the toolbar can register itself + the editor can outline
    /// the overlay/status windows (composition root passes the concrete WindowService).</summary>
    public void SetWindows(WindowService windows) => _windows = windows;

    // Show the toolbar on edit-enter (registering it the first time); hide on exit.
    private void ShowToolbar()
    {
        if (_toolbarWindow == null && _windows != null)
        {
            // GlassMenu chrome (ShowTitleBar=false) = frosted rounded panel, no title bar (Borderless is
            // self-framed → no bg). Fixed width, centred on the current resolution; flexible spacers centre
            // the content within the panel.
            const float w = 1180f;
            var res = _input.CurrentResolution;
            var spec = new WindowSpec("framework.layout-toolbar", "",
                new WindowRect((res.Width - w) / 2f, 12f, w, 0f),
                WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            { StartVisible = false, ShowTitleBar = false, Draggable = true };
            _toolbarWindow = _windows.Register(new WindowRegistration(spec, BuildToolbarRoot()));
        }
        _toolbarWindow?.SetVisible(true);
    }

    private void HideToolbar() => _toolbarWindow?.SetVisible(false);

    // The toolbar as a single Row of framework elements. Funcs re-pull live state (selection, active slot,
    // filter) on the window's refresh; Buttons' OnClick call the unchanged behaviour below.
    private HudElement BuildToolbarRoot()
    {
        // Text cells get explicit widths so they don't collapse/wrap inside the bar (window TextElements wrap
        // at minWidth=0; buttons size to their own labels). Leading/trailing flexible Spacers centre the
        // content block within the fixed-width panel.
        var items = new List<HudElement>
        {
            new SpacerElement(),
            new TextElement(() => "Layout edit mode", () => _theme.Colors.MenuAccent, Emphasis: true, Width: 132f),
            new SpacerElement(10f),
            FilterChip("All", EditFilter.All),
            FilterChip("Stellar", EditFilter.StellarOnly),
            FilterChip("Game UI", EditFilter.GameUiOnly),
            new SpacerElement(10f),
            new TextElement(() => "Slot:", () => _theme.Colors.MenuMuted, Width: 34f),
        };
        for (int i = 0; i < MaxSlots; i++)
        {
            var idx = i;
            items.Add(new ConditionalElement(() => idx < _storage.SlotCount,
                new ButtonElement(() => _storage.GetSlotName(idx), () => SwitchToSlot(idx),
                    Active: () => idx == _storage.ActiveSlot)));
        }
        items.Add(new SpacerElement(10f));
        items.Add(new TextElement(() => $"Selected: {_editor.SelectedWindowId ?? "(none)"}", Width: 210f));
        items.Add(new SpacerElement(10f));
        items.Add(new ButtonElement(() => "Reset selected",
            () => { if (_editor.SelectedWindowId is { } s) ResetWindow(s); },
            Enabled: () => _editor.SelectedWindowId != null));
        items.Add(new ButtonElement(() => "Reset all", ResetAllWindows));
        items.Add(new SpacerElement(10f));
        items.Add(new TextElement(() => "Shift+` to exit", () => _theme.Colors.MenuMuted, Width: 104f));
        items.Add(new SpacerElement());
        // Column wrap so the Row gets full panel width (childForceExpandWidth) → the flex spacers can centre.
        return new ColumnElement(new HudElement[] { new RowElement(items.ToArray(), Gap: 6f) });
    }

    private HudElement FilterChip(string label, EditFilter filter)
        => new ButtonElement(() => label, () => _editFilter = filter, Active: () => _editFilter == filter);

    private void SwitchToSlot(int slotIndex)
    {
        if (slotIndex == _storage.ActiveSlot) return;
        _storage.SetActiveSlot(slotIndex);
        // A layout slot captures the whole screen arrangement, so move the game HUD to this slot's saved
        // native positions too (Phase 9a editor). uGUI windows/HUDs reapply via their own services.
        _nativeUi?.ReapplyForActiveSlot(_input.CurrentResolution);
        _log.Info($"[LayoutEditor] switched to slot {slotIndex} ({_storage.GetSlotName(slotIndex)})");
    }

    // "Reset selected" — reset whichever element is selected, whatever its kind. The id namespaces don't
    // collide (native UI = "gameui.*", mod HUDs/windows = plugin ids) and each service no-ops on an unknown id,
    // so fanning the reset across all three is safe and avoids the editor tracking element types.
    private void ResetWindow(string windowId)
    {
        _nativeUi?.ResetToOriginal(windowId);
        _hud?.ResetRect(windowId);
        _windows?.ResetRect(windowId);
    }

    // "Reset all" = every element currently OUTLINED in edit mode (respecting the active filter), restored to
    // its default pose: native game UI + mod HUDs + mod windows. Ids are snapshotted before resetting so a
    // reset never mutates a collection mid-enumeration.
    private void ResetAllWindows()
    {
        var ids = new List<string>();
        if (ShouldOutlineGameUi && _nativeUi is not null)
            foreach (var e in _nativeUi.Entries) if (e.IsResolved) ids.Add(e.Descriptor.Id);
        if (ShouldOutlineStellar)
        {
            if (_hud is not null)     foreach (var (id, _) in _hud.ShownRects())      ids.Add(id);
            if (_windows is not null) foreach (var (id, _) in _windows.EditableRects()) ids.Add(id);
        }
        foreach (var id in ids) ResetWindow(id);
        _log.Info($"[LayoutEditor] reset {ids.Count} elements to defaults");
    }
}
