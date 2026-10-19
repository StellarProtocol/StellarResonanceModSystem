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
    private WindowService? _windows;        // bound late via SetWindows (concrete: editor enumerates its overlay windows)
    private IWindowControl? _toolbarWindow; // the uGUI toolbar window; registered lazily on first edit-enter

    // One-shot toolbar PRE-WARM. The toolbar's whole uGUI tree (glass panel, two dropdowns, buttons, the first
    // font-atlas + asset bake) is built on its first MOUNT — which, left lazy, happens inside the frame the user
    // first enters edit mode, so that first enter visibly hitches. (Later enters are smooth because a hidden window
    // is PARKED, not destroyed — WindowService.ParkOrDestroy keeps the tree and reuses it.) We pay that build cost
    // once during load instead: mount the toolbar while forcing it invisible, then park it. See PrewarmToolbar.
    //   _prewarming — transient; while true the toolbar's ShouldRender returns false so the mounted root stays
    //                 DEACTIVATED (never flashes on screen, in ANY game phase). Mount still builds the tree —
    //                 ShouldRender only gates post-mount ACTIVATION (SafeApply), not the mount/build.
    //   _prewarmPhase — 0 = pending (not started), 1 = mounting (waiting a few ticks for WindowService.Tick to
    //                   mount + apply regardless of tick ordering), 2 = done (never runs again).
    private bool _prewarming;
    private int _prewarmPhase;   // 0 = pending, 1 = mounting, 2 = done
    private int _prewarmTicks;

    // Edit-mode element filter (Phase 9a + per-plugin). All = Stellar windows + game UI; StellarOnly / GameUiOnly
    // suppress the other category; Plugin restricts the Stellar side to ONE plugin's windows (no game UI). Read by
    // ShouldOutlineStellar / ShouldOutlineGameUi for the category gate, and WindowOwnerMatchesFilter for the
    // per-plugin narrowing applied in both outlines (AddWindowServiceItems) and click-select (TrySelectWindow).
    private enum EditFilter { All = 0, StellarOnly = 1, GameUiOnly = 2, Plugin = 3 }
    private EditFilter _editFilter = EditFilter.All;
    private string? _filterPluginGuid;   // the selected plugin's guid while _editFilter == Plugin
    // Plugin mode still outlines Stellar windows (narrowed to the one plugin), so the Stellar side stays on unless
    // GameUiOnly; the game-UI side shows only in All / GameUiOnly (never while a specific plugin is selected).
    private bool ShouldOutlineStellar => _editFilter != EditFilter.GameUiOnly;
    private bool ShouldOutlineGameUi  => _editFilter is EditFilter.All or EditFilter.GameUiOnly;

    /// <summary>Per-plugin narrowing for the Stellar side: true unless a specific plugin is selected AND this
    /// window isn't owned by it. Applied on top of <see cref="ShouldOutlineStellar"/> in both the outline build
    /// and the click-select so only that plugin's windows highlight/select/drag.</summary>
    private bool WindowOwnerMatchesFilter(string windowId)
        => _editFilter != EditFilter.Plugin || (_windows != null && _windows.OwnerOf(windowId) == _filterPluginGuid);

    /// <summary>Live editability predicate for a mod window — the SAME rule the outline/select use: a window is
    /// draggable/resizable in edit mode only when its category is outlined (<see cref="ShouldOutlineStellar"/>,
    /// false in GameUiOnly) AND it passes the per-plugin filter (<see cref="WindowOwnerMatchesFilter"/>). Bound to
    /// <see cref="Stellar.Infrastructure.Unity.LayoutEditGate.EditableWindowFilter"/> so the WindowInteractionTicker's
    /// edit-only drag/resize narrows to exactly the windows the outline/select target.</summary>
    private bool IsWindowEditableNow(string id) => ShouldOutlineStellar && WindowOwnerMatchesFilter(id);

    /// <summary>Bind the framework window service so the toolbar can register itself + the editor can outline
    /// the overlay/status windows (composition root passes the concrete WindowService).</summary>
    public void SetWindows(WindowService windows) => _windows = windows;

    // Show the toolbar on edit-enter; hide on exit. Entering edit mode mid-pre-warm cleanly takes over: clear the
    // warm-up flag + mark it done BEFORE SetVisible(true) so the suppressing ShouldRender releases and no stuck
    // state or double logic survives. EnsureToolbarRegistered is idempotent, so this is safe whether the warm-up
    // already registered the window or not.
    private void ShowToolbar()
    {
        EnsureToolbarRegistered();
        _prewarming = false;
        _prewarmPhase = 2;
        _toolbarWindow?.SetVisible(true);
    }

    // Register the toolbar window the first time (idempotent). Separated from ShowToolbar so the pre-warm path can
    // mount it without showing it — construction (the expensive spec/tree build at first mount) is the same either way.
    private void EnsureToolbarRegistered()
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
            // Widened to seat the selection DROPDOWN (60px label + 280px dropdown + the 6px gap) that replaced the old
            // 210px "Selected: <id>" text, as well as the extra "Reset filtered" button — without the flex-centred
            // content block clipping against the glass panel edges (the content CLIPS at the panel edges if it
            // overflows). Sized for the longest-locale labels (id/th reset captions run longer than English).
            const float w = 1480f;
            var spec = new WindowSpec(Stellar.Infrastructure.Game.WindowBuilder.LayoutToolbarWindowId, "",
                new WindowRect(0f, 12f, w, 0f),
                WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            {
                // Framework chrome — hide over the loading screen; otherwise available in every phase. EditModeDragOnly
                // stays false (the bar only exists in edit mode → free drag is correct, and it stays out of the editor's
                // own outlined/selectable set — EditableElements yields only EditModeDragOnly windows).
                // While pre-warming, force this false so the mounted toolbar NEVER activates (never flashes) — the
                // mount still builds the tree; ShouldRender only gates post-mount activation. Cleared once the
                // warm-up parks the window (and immediately on a real edit-enter via ShowToolbar).
                ShouldRender = () => !_prewarming && (_clientState.UiState & GameUIState.Loading) == 0,
                StartVisible = false,
                ShowTitleBar = false,
                Draggable = true,
                Anchor = WindowAnchor.Top,
                OverrideSortingOrder = ToolbarOverrideSortingOrder,
                // The toolbar's own position is GLOBAL, not part of any layout slot: switching slots in the toolbar's
                // own dropdown must never move or re-hide the toolbar. SlotIndependentLayout persists its drag in a
                // single resolution-keyed bucket and excludes it from the slot-switch reapply (it still repositions
                // from that bucket on a resolution change). Its visibility is owned by ShowToolbar/HideToolbar.
                SlotIndependentLayout = true,
            };
            _toolbarWindow = _windows.Register(new WindowRegistration(spec, BuildToolbarRoot()));
        }
    }

    // One-shot warm-up state machine, pumped once per tick from TickInput (BEFORE the IsEditing branch, so it runs
    // whether or not editing). Mounts the toolbar invisibly during load so the FIRST real edit-enter just un-parks an
    // already-built window instead of building the whole tree in that frame.
    private void PrewarmToolbar()
    {
        if (_prewarmPhase == 2) return;
        if (_windows == null) return;   // not wired yet — retry next tick
        // Gate the START on the window canvas being ready so the mount actually succeeds and the anchored position
        // applies correctly (a mount before the CanvasScaler settles would defer its layout). Reuse CanvasScaleReady
        // — the same ready-gate the Host's layout apply uses; no new WindowService surface needed.
        if (!_windows.CanvasScaleReady) return;   // canvas not up yet — retry next tick
        switch (_prewarmPhase)
        {
            case 0:   // pending → begin mounting, suppressed
                EnsureToolbarRegistered();
                _prewarming = true;                 // ShouldRender stays false → mounts but never activates (no flash)
                _toolbarWindow?.SetVisible(true);    // Visible=true → WindowService.TickEntry mounts (builds the tree)
                _prewarmTicks = 0;
                _prewarmPhase = 1;
                break;
            case 1:   // mounting → give WindowService.Tick a few ticks to mount + apply regardless of tick ordering
                if (++_prewarmTicks >= 3)
                {
                    _toolbarWindow?.SetVisible(false);   // Visible=false → parked (tree kept, reused on the real show)
                    _prewarming = false;
                    _prewarmPhase = 2;                   // done — never runs again
                }
                break;
        }
    }

    // Also close any open toolbar dropdown: its popup is a scene-root canvas, not a child of the toolbar window,
    // so SetVisible(false) alone would leave a stray option list on screen (belt-and-suspenders, idempotent).
    private void HideToolbar() { _windows?.CloseOpenPopups(); _toolbarWindow?.SetVisible(false); }

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
            // One dropdown replaces the 3 filter chips: << All >>, << Stellar >>, << Game UI >>, then one row per
            // plugin that owns an editable window (friendly localized name). MaxVisibleRows caps + scrolls the list.
            new DropdownElement(FilterSelectedIndex, FilterOptions, OnFilterSelected, Width: 150f) { MaxVisibleRows = 3 },
            new SpacerElement(10f),
            new TextElement(() => _text!.T("layout.toolbar.slot"), () => _theme.Colors.MenuMuted, Width: 34f),
            // One dropdown replaces the per-slot buttons (switch-only — add/remove stays in Settings -> Layout).
            // SlotNames is a live snapshot rebuilt on any add/remove/rename, so renames and count changes reflect.
            // SwitchToSlot routes through SetActiveSlot, whose SlotsChanged reapply now moves BOTH native + mod
            // windows. MaxVisibleRows == MaxSlotCount (8), so the list never actually scrolls — kept for parity
            // with the filter dropdown. The popup self-elevates above the override-sorting toolbar (fw 1626025).
            new DropdownElement(() => _storage.ActiveSlot, () => _storage.SlotNames, SwitchToSlot, Width: 140f) { MaxVisibleRows = 8 },
        };
        items.Add(new SpacerElement(10f));
        items.Add(new TextElement(() => _text!.T("layout.toolbar.selectedLabel"), () => _theme.Colors.MenuMuted, Width: 60f));
        items.Add(new DropdownElement(SelectionSelectedIndex, SelectionOptions, OnSelectionPicked, Width: 280f) { MaxVisibleRows = 12 });
        items.Add(new SpacerElement(10f));
        items.Add(new ButtonElement(() => _text!.T("layout.toolbar.resetSelected"),
            () => { if (_editor.SelectedWindowId is { } s) ResetWindow(s); },
            Enabled: () => _editor.SelectedWindowId != null));
        // Logical order narrowest → filter-scoped → broadest: selected, then the active-filter set, then everything.
        // Always enabled — when the filter is All it harmlessly equals Reset all (same resulting set), as expected.
        items.Add(new ButtonElement(() => _text!.T("layout.toolbar.resetFiltered"), ResetFilteredWindows));
        items.Add(new ButtonElement(() => _text!.T("common.resetAll"), ResetAllWindows));
        items.Add(new SpacerElement(10f));
        items.Add(new ButtonElement(() => _text!.T("common.exit"), () => _editor.ToggleEditMode()));
        items.Add(new SpacerElement());
        // Column wrap so the Row gets full panel width (childForceExpandWidth) → the flex spacers can centre.
        return new ColumnElement(new HudElement[] { new RowElement(items.ToArray(), Gap: 6f) });
    }

    // The currently-selectable layout objects, in a STABLE deterministic order (each group sorted by label, so the
    // dropdown's Options() and SelectionSelectedIndex() always agree frame-to-frame regardless of dictionary order).
    // Mirrors exactly what click-select can target under the active filter: native game-UI entries (ShouldOutlineGameUi,
    // resolved+visible) then mod windows (ShouldOutlineStellar, visible via EditableRects, per-plugin filter).
    private System.Collections.Generic.IReadOnlyList<(string Id, string Label)> SelectableObjects()
    {
        var list = new List<(string Id, string Label)>();
        if (ShouldOutlineGameUi && _nativeUi is not null)
        {
            var g = new List<(string Id, string Label)>();
            foreach (var e in _nativeUi.Entries)
                if (e.IsResolved && e.Visible) g.Add((e.Descriptor.Id, _text!.T(e.Descriptor.DisplayNameKey)));
            g.Sort((a, b) => string.Compare(a.Label, b.Label, System.StringComparison.CurrentCultureIgnoreCase));
            list.AddRange(g);
        }
        if (ShouldOutlineStellar && _windows is not null)
        {
            var g = new List<(string Id, string Label)>();
            foreach (var (id, _) in _windows.EditableRects())
                if (WindowOwnerMatchesFilter(id)) g.Add((id, id));
            g.Sort((a, b) => string.Compare(a.Label, b.Label, System.StringComparison.CurrentCultureIgnoreCase));
            list.AddRange(g);
        }
        return list;
    }

    // Dropdown rows: 0 = "(none)" (clears selection), then one row per selectable object.
    private System.Collections.Generic.IReadOnlyList<string> SelectionOptions()
    {
        var opts = new List<string> { _text!.T("common.none") };
        foreach (var (_, label) in SelectableObjects()) opts.Add(label);
        return opts;
    }

    // Current selection → row index. null (or a selection not in the current filtered/visible set) → 0 = "(none)".
    private int SelectionSelectedIndex()
    {
        if (_editor.SelectedWindowId is not { } sel) return 0;
        var objs = SelectableObjects();
        for (int i = 0; i < objs.Count; i++) if (objs[i].Id == sel) return 1 + i;
        return 0;
    }

    // Pick: row 0 clears selection; rows 1.. select that object (same effect as clicking it in the scene).
    private void OnSelectionPicked(int index)
    {
        if (index <= 0) { _editor.SetSelectedWindow(null); return; }
        var objs = SelectableObjects();
        var i = index - 1;
        if (i >= 0 && i < objs.Count) _editor.SelectWindow(objs[i].Id);
    }

    // The plugins that own an editable window, sorted by display name (WindowService sorts); indices here map to
    // dropdown rows 3.. . Re-read live each poll so a plugin that registers/unloads mid-session appears/vanishes.
    private System.Collections.Generic.IReadOnlyList<(string Guid, string Name)> FilterPlugins()
        => _windows?.EditableWindowPlugins() ?? System.Array.Empty<(string, string)>();

    // Dropdown options built live: 0 = << All >>, 1 = << Stellar >>, 2 = << Game UI >>, then one row per plugin.
    // Only the 3 specials get the << … >> decoration; plugin captions are the plain (already-localized) name.
    private System.Collections.Generic.IReadOnlyList<string> FilterOptions()
    {
        var opts = new List<string>
        {
            "<< " + _text!.T("hotkeys.filter.all") + " >>",
            "<< Stellar >>",                               // brand name — kept English in every language
            "<< " + _text!.T("tab.gameui") + " >>",
        };
        foreach (var (_, name) in FilterPlugins()) opts.Add(name);
        return opts;
    }

    // Current filter → selected dropdown index. A selected plugin that has since unloaded (not in the live list)
    // falls back to index 1 (<< Stellar >> = all mod windows), the nearest still-valid meaning.
    private int FilterSelectedIndex()
    {
        switch (_editFilter)
        {
            case EditFilter.All: return 0;
            case EditFilter.StellarOnly: return 1;
            case EditFilter.GameUiOnly: return 2;
            default:
                var plugins = FilterPlugins();
                for (int i = 0; i < plugins.Count; i++)
                    if (plugins[i].Guid == _filterPluginGuid) return 3 + i;
                return 1;
        }
    }

    // Dropdown pick → filter state. 0/1/2 = the specials (clear the plugin guid); 3.. selects that plugin.
    private void OnFilterSelected(int index)
    {
        switch (index)
        {
            case 0: _editFilter = EditFilter.All; _filterPluginGuid = null; break;
            case 1: _editFilter = EditFilter.StellarOnly; _filterPluginGuid = null; break;
            case 2: _editFilter = EditFilter.GameUiOnly; _filterPluginGuid = null; break;
            default:
                var plugins = FilterPlugins();
                var pi = index - 3;
                if (pi >= 0 && pi < plugins.Count) { _editFilter = EditFilter.Plugin; _filterPluginGuid = plugins[pi].Guid; }
                break;
        }
    }

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

    // "Reset all" = EVERY editable element restored to its default pose, REGARDLESS of the active filter: every
    // resolved native game-UI entry + every editable mod window (no ShouldOutline* gate, no per-plugin filter).
    // Enumerate EditableElements (NOT EditableRects): EditableRects yields only VISIBLE windows, so a window the
    // user HID via the eye toggle would never make the reset list and would stay hidden — iterating the full
    // (hidden-inclusive) set means ResetWindow also clears a hidden window's persisted-hidden state (ResetRect
    // force-shows it). Ids are snapshotted before resetting so a reset never mutates a collection mid-enumeration.
    // (Contrast ResetFilteredWindows, which honours the filter — the two buttons are deliberately separated.)
    private void ResetAllWindows()
    {
        var ids = new List<string>();
        if (_nativeUi is not null)
            foreach (var e in _nativeUi.Entries) if (e.IsResolved) ids.Add(e.Descriptor.Id);
        if (_windows is not null)
            foreach (var el in _windows.EditableElements()) ids.Add(el.Id);
        foreach (var id in ids) ResetWindow(id);
        _log.Info($"[LayoutEditor] reset all {ids.Count} elements to defaults");
    }

    // "Reset filtered" = every element currently OUTLINED in edit mode (respecting the active filter), restored to
    // its default pose: native game UI only when ShouldOutlineGameUi, mod windows only when ShouldOutlineStellar AND
    // the per-plugin filter matches. When the filter is All this equals Reset all; narrower filters scope it down.
    // Enumerate EditableElements (NOT EditableRects): EditableRects yields only VISIBLE windows, so a window the
    // user HID via the eye toggle would never make the reset list and would stay hidden — iterating the full
    // (hidden-inclusive) set means a hidden window's visibility is also reset (ResetRect force-shows it).
    // Ids are snapshotted before resetting so a reset never mutates a collection mid-enumeration.
    private void ResetFilteredWindows()
    {
        var ids = new List<string>();
        if (ShouldOutlineGameUi && _nativeUi is not null)
            foreach (var e in _nativeUi.Entries) if (e.IsResolved) ids.Add(e.Descriptor.Id);
        if (ShouldOutlineStellar)
        {
            if (_windows is not null) foreach (var el in _windows.EditableElements()) if (WindowOwnerMatchesFilter(el.Id)) ids.Add(el.Id);
        }
        foreach (var id in ids) ResetWindow(id);
        _log.Info($"[LayoutEditor] reset filtered {ids.Count} elements to defaults");
    }
}
