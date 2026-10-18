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
            // GlassMenu chrome (ShowTitleBar=false) = frosted rounded panel, no title bar; with ShowTitleBar=false
            // + Draggable the WHOLE frame becomes the drag handle (BuildGlassChrome), so the user can reposition the
            // bar by grabbing its empty/heading/spacer area while the slot/reset/exit BUTTONS still click normally
            // (a zero-movement press never translates the window; a press+drag moves it — same model as the
            // Borderless launcher). Width fixed at w; WindowAnchor.Top auto-places it top-CENTRED live for the
            // current resolution + UI scale, and WindowService persists any drag PER-RESOLUTION (PersistIfSettled)
            // and restores it on show (ApplySavedRect) — so no per-tick re-centre is needed or wanted (that would
            // clobber a user drag). OverrideSortingOrder lifts the bar above the edit chrome (see the const).
            const float w = 1180f;
            var spec = new WindowSpec(Stellar.Infrastructure.Game.WindowBuilder.LayoutToolbarWindowId, "",
                new WindowRect(0f, 12f, w, 0f),
                WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            {
                // Framework chrome — hide over the loading screen; otherwise available in every phase. EditModeDragOnly
                // stays false (the bar only exists in edit mode → free drag is correct, and it stays out of the editor's
                // own outlined/selectable set — EditableElements yields only EditModeDragOnly windows).
                ShouldRender = () => (_clientState.UiState & GameUIState.Loading) == 0,
                StartVisible = false,
                ShowTitleBar = false,
                Draggable = true,
                Anchor = WindowAnchor.Top,
                OverrideSortingOrder = ToolbarOverrideSortingOrder,
            };
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
        // _text is always bound (SetLocalization, called before SetWindows below it in Wiring.Settings.cs)
        // by the time a player can ever enter edit mode and reach this method — use it directly rather than
        // keeping a second, driftable copy of the English text as a "??" fallback.
        var items = new List<HudElement>
        {
            new SpacerElement(),
            new TextElement(() => _text!.T("layout.toolbar.heading"), () => _theme.Colors.MenuAccent, Emphasis: true, Width: 132f),
            new SpacerElement(10f),
            FilterChip(() => _text!.T("hotkeys.filter.all"), EditFilter.All),
            FilterChip(() => "Stellar", EditFilter.StellarOnly),   // brand name — kept English in every language
            FilterChip(() => _text!.T("tab.gameui"), EditFilter.GameUiOnly),
            new SpacerElement(10f),
            new TextElement(() => _text!.T("layout.toolbar.slot"), () => _theme.Colors.MenuMuted, Width: 34f),
        };
        for (int i = 0; i < MaxSlots; i++)
        {
            var idx = i;
            items.Add(new ConditionalElement(() => idx < _storage.SlotCount,
                new ButtonElement(() => _storage.GetSlotName(idx), () => SwitchToSlot(idx),
                    Active: () => idx == _storage.ActiveSlot)));
        }
        items.Add(new SpacerElement(10f));
        items.Add(new TextElement(SelectedLabel, Width: 210f));
        items.Add(new SpacerElement(10f));
        items.Add(new ButtonElement(() => _text!.T("layout.toolbar.resetSelected"),
            () => { if (_editor.SelectedWindowId is { } s) ResetWindow(s); },
            Enabled: () => _editor.SelectedWindowId != null));
        items.Add(new ButtonElement(() => _text!.T("common.resetAll"), ResetAllWindows));
        items.Add(new SpacerElement(10f));
        items.Add(new ButtonElement(() => _text!.T("common.exit"), () => _editor.ToggleEditMode()));
        items.Add(new SpacerElement());
        // Column wrap so the Row gets full panel width (childForceExpandWidth) → the flex spacers can centre.
        return new ColumnElement(new HudElement[] { new RowElement(items.ToArray(), Gap: 6f) });
    }

    private string SelectedLabel()
    {
        var id = _editor.SelectedWindowId ?? _text!.T("common.none");
        return _text!.TFormat("layout.toolbar.selected", id);
    }

    private HudElement FilterChip(System.Func<string> label, EditFilter filter)
        => new ButtonElement(label, () => _editFilter = filter, Active: () => _editFilter == filter);

    private void SwitchToSlot(int slotIndex)
    {
        if (slotIndex == _storage.ActiveSlot) return;
        // SetActiveSlot fires LayoutStorage.SlotsChanged; the composition root subscribes BOTH the native game HUD
        // (NativeUiService.ReapplyForActiveSlot) and the mod windows (WindowService.ReapplyLayout) to it, so a slot
        // switch reapplies the whole screen arrangement from one place — no explicit per-call-site reapply here.
        _storage.SetActiveSlot(slotIndex);
        _log.Info($"[LayoutEditor] switched to slot {slotIndex} ({_storage.GetSlotName(slotIndex)})");
    }

    // "Reset selected" — reset whichever element is selected, whatever its kind. The id namespaces don't
    // collide (native UI = "gameui.*", mod HUDs/windows = plugin ids) and each service no-ops on an unknown id,
    // so fanning the reset across all three is safe and avoids the editor tracking element types.
    private void ResetWindow(string windowId)
    {
        _nativeUi?.ResetToOriginal(windowId);
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
            if (_windows is not null) foreach (var (id, _) in _windows.EditableRects()) ids.Add(id);
        }
        foreach (var id in ids) ResetWindow(id);
        _log.Info($"[LayoutEditor] reset {ids.Count} elements to defaults");
    }
}
