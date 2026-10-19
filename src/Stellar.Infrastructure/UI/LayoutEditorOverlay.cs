using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using UnityEngine;
using UnityRect = UnityEngine.Rect;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Renders the edit-mode chrome (per-window outline, top-left label, bottom-right
/// corner handle, selection-highlight) over every visible editable element (uGUI
/// windows + native game UI) when <see cref="LayoutEditorService.IsEditing"/>
/// is true. Driven from the framework tick (see <see cref="TickInput"/>).
/// </summary>
/// <remarks>
/// <para>
/// Colour by state: orange (unselected) / yellow (selected) / red (errored).
/// </para>
/// <para>
/// Input handling (driven from the framework tick via TickInput): LeftMousePressedSinceTick -&gt; SelectWindow + BeginDrag for
/// the first window containing the pointer. LeftMouseDown + dragging -&gt;
/// UpdateDrag with snap candidates (other window rects + screen-edge fractions);
/// selected window's CurrentRect updates immediately so the live overlay tracks.
/// Mouse release -&gt; EndDrag commits the final rect via LayoutStorage.
/// </para>
/// <para>
/// Top-center toolbar (drawn AFTER per-window chrome so it sits on top): mint
/// "Layout edit mode" label, selected-window readout, slot picker (4 toggle
/// buttons), [Reset selected] + [Reset all] buttons, and an [Exit] button that
/// leaves edit mode. Toolbar rendering lives in <c>LayoutEditorOverlay.Toolbar.cs</c>.
/// </para>
/// </remarks>
internal sealed partial class LayoutEditorOverlay
{
    private static readonly Color OutlineUnselected = new(0.96f, 0.64f, 0.25f, 0.85f);
    private static readonly Color OutlineSelected   = new(1.00f, 0.82f, 0.40f, 1.00f);
    private static readonly Color OutlineErrored    = new(1.00f, 0.30f, 0.30f, 0.85f);

    private readonly LayoutEditorService _editor;
    private readonly IInputGateway _input;
    private readonly LayoutStorage _storage;
    private readonly ITheme _theme;
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;   // for the toolbar window's loading-screen ShouldRender gate
    private NativeUiService? _nativeUi;   // bound late via SetNativeUi (Phase 9a)
    // Localized toolbar/outline text — bound late via SetLocalization (Wiring.Settings.cs, after the framework
    // localization engine exists) rather than a ctor param: this ctor is already at the STELLAR0004 cap (6 deps).
    private ILocalization? _text;

    // Stage B: per-element outline/handle/label now render on a uGUI overlay canvas (LayoutEditChrome),
    // driven from the tick — not IMGUI GUI.DrawTexture. The toolbar is still IMGUI (Stage C migrates it).
    private readonly Stellar.Infrastructure.Game.LayoutEditChrome _chrome = new();
    private readonly List<Stellar.Infrastructure.Game.EditChromeItem> _chromeItems = new(16);
    private bool _wasEditing;

    // Edit-mode input blocker. Sits just ABOVE the game's UI canvases but BELOW Stellar's interactive
    // window/toolbar layer, so while editing it absorbs clicks bound for native game UI (chat, profile, …) —
    // stopping the press from leaking through and opening chat / a profile while you drag an element — without
    // stealing clicks from the toolbar or draggable mod windows. The editor drives its own select/drag from
    // raw input polling (IInputGateway) + rect hit-tests, so absorbing the EventSystem click doesn't disable it.
    // Layering: game UI (<32750) < blocker (32749) < HUD (32750) < Window/toolbar (32755) < chrome (32758).
    private const int EditBlockerSortingOrder = 32749;
    private const int InteractiveLayerMinOrder = 32755;
    // The toolbar window renders on its own override-sorting canvas (WindowSpec.OverrideSortingOrder) at this
    // absolute order — ABOVE the edit chrome (32758) so its buttons draw over the outlines, BELOW the top input
    // blocker (32760). Shared by ShowToolbar (sets the spec) and IsPointerOverToolbar (the precise press guard).
    private const int ToolbarOverrideSortingOrder = 32759;
    private readonly UGuiInputBlocker _editInputBlocker = new(EditBlockerSortingOrder);

    public LayoutEditorOverlay(LayoutEditorService editor, IInputGateway input,
                                LayoutStorage storage, ITheme theme, IPluginLog log, IClientState clientState)
    {
        _editor = editor;
        _input = input;
        _storage = storage;
        _theme = theme;
        _log = log;
        _clientState = clientState;
        _chrome.Warn = log.Warning;
    }

    /// <summary>True while layout edit-mode is active — its chrome draws through OnGUI,
    /// so the host counts it as live IMGUI content for the overlay lifecycle.</summary>
    public bool IsEditing => _editor.IsEditing;

    /// <summary>The layout-edit chrome canvas root, or null outside edit mode (photo overlay hide).</summary>
    public GameObject? ChromeCanvas => _chrome.CanvasObject;

    /// <summary>Raised when the chrome canvas is created (edit mode entered).</summary>
    public event System.Action? ChromeCanvasCreated
    {
        add => _chrome.CanvasCreated += value;
        remove => _chrome.CanvasCreated -= value;
    }

    /// <summary>Phase 9a: bind the native-UI service so Shift+` also outlines + drags game HUD elements.</summary>
    public void SetNativeUi(NativeUiService nativeUi) => _nativeUi = nativeUi;

    /// <summary>Bind the framework localization façade so the toolbar heading/filters/buttons and the
    /// per-entry "[Game UI] …" outline labels follow the active language.</summary>
    public void SetLocalization(ILocalization text) => _text = text;

    /// <summary>Input half — driven from the framework TICK (not OnGUI), so edit-mode select/drag works at the
    /// throttled tick rate and no longer depends on the IMGUI OnGUI handler firing. Stage A of the layout-editor
    /// uGUI migration: rendering still happens in <see cref="Render"/> (IMGUI) for now; only input moved here.</summary>
    /// <summary>Drives edit mode entirely from the framework tick (no OnGUI). On enter: show the uGUI toolbar
    /// window + start the chrome; each tick: ProcessInput then push the live outline list; on exit: tear down.
    /// Fully IMGUI-free (Stage C/D of the layout-editor migration).</summary>
    public void TickInput()
    {
        // Gate edit-only window drag (overlay/status windows move only in edit mode; popup dialogs drag freely).
        Stellar.Infrastructure.Unity.LayoutEditGate.IsEditing = _editor.IsEditing;
        // Also narrow edit-only drag/resize to the currently-filtered plugin's windows — the SAME rule the
        // outline/select use (ShouldOutlineStellar && WindowOwnerMatchesFilter). Tracks live filter state each tick.
        Stellar.Infrastructure.Unity.LayoutEditGate.EditableWindowFilter = IsWindowEditableNow;
        if (_editor.IsEditing)
        {
            if (!_wasEditing) { ShowToolbar(); _editInputBlocker.SetActive(true); _nativeUi?.DumpRectDiagnostics(_log.Info); }   // bug #5 evidence (self-gates on diagnostics)
            ProcessInput();
            SyncChrome();   // build the editable-element list + push to the uGUI overlay (EnsureCanvas self-heals)
        }
        else if (_wasEditing)
        {
            _chrome.Teardown();   // destroy the overlay canvas on edit-mode exit
            // A toolbar dropdown (filter/slot) left open when edit mode exits lives on its OWN scene-root popup
            // canvas, so hiding the toolbar below does NOT tear it down — close it explicitly or the option list
            // stays on screen after exit (Alt+E / Exit-button-while-open bug).
            _windows?.CloseOpenPopups();
            HideToolbar();
            _editInputBlocker.SetActive(false);   // stop absorbing game-UI clicks once out of edit mode
            Stellar.Infrastructure.Unity.EditDragArbiter.EditorDragActive = false;
            Stellar.Infrastructure.Unity.LayoutEditGate.EditableWindowFilter = null;   // no filter outside edit mode
        }
        _wasEditing = _editor.IsEditing;
    }

    // Build the live editable-element list (windows + native game-UI, respecting filters + selection)
    // and push it to the uGUI overlay. Runs each tick AFTER ProcessInput so outlines track an in-progress drag.
    private void SyncChrome()
    {
        // No per-tick re-centre here anymore: the toolbar is a Draggable, Top-anchored window, so WindowService
        // owns its placement — it restores a user-dragged position (persisted per-resolution) or falls back to a
        // live top-centre via WindowAnchor.Top, and re-applies on a resolution / UI-scale change
        // (ReclampLayoutOnResolutionChange → ReapplyLayout). Re-centring every tick would clobber a user drag.
        _chromeItems.Clear();
        if (ShouldOutlineStellar && _windows != null) AddWindowServiceItems(_chromeItems);
        if (ShouldOutlineGameUi) AddNativeUiItems(_chromeItems);
        _chrome.Sync(_chromeItems);
    }

    // Edit-mode outline + label over each shown overlay/status WindowService window (AutoNav etc.) so they
    // have visual parity with the HUDs. These drag in edit mode via the gated WindowInteractionTicker; the
    // editor doesn't grab them yet, so the outline stays unselected-orange (yellow-on-select is a follow-up).
    private void AddWindowServiceItems(List<Stellar.Infrastructure.Game.EditChromeItem> items)
    {
        foreach (var el in _windows!.EditableElements())   // incl. hidden (dimmed re-enable outline)
        {
            if (!WindowOwnerMatchesFilter(el.Id)) continue;   // per-plugin filter: only the selected plugin's windows
            var colour = _editor.SelectedWindowId == el.Id ? OutlineSelected : OutlineUnselected;
            items.Add(new Stellar.Infrastructure.Game.EditChromeItem(el.Rect, colour, el.Id, el.Id, el.Visible, el.CanHide, el.Resizable));
        }
    }

    /// <summary>Source of the currently-dragged element — picks the commit path on mouse-up.</summary>
    private enum DragKind { None, NativeUi }
    private DragKind _dragKind = DragKind.None;

    private void ProcessInput()
    {
        if (_input.LeftMousePressedSinceTick) HandlePressed();
        else if (_input.LeftMouseDown && _editor.IsDragging) HandleDragMove();
        else if (!_input.LeftMouseDown && _editor.IsDragging) HandleDragRelease();
    }

    // True when the pointer is over Stellar's INTERACTIVE uGUI layer (toolbar / draggable mod windows at
    // sortingOrder >= InteractiveLayerMinOrder). It deliberately IGNORES the edit-mode input blocker (32749)
    // and the game's own UI (<32750), so native/HUD elements stay grabbable while the blocker absorbs their
    // click from the game. RaycastAll + a sorting-order filter (not IsPointerOverGameObject, which the
    // full-screen blocker would make true everywhere); called once per press, so the alloc is negligible.
    private readonly Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult> _uiRaycastScratch = new();
    private bool IsPointerOverUgui()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es == null) return false;
        var (px, py) = _input.PointerPosition;
        // PointerPosition is top-left/y-down; RaycastAll wants y-up → flip Y (X is unchanged).
        var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = new Vector2(px, UnityEngine.Screen.height - py) };
        _uiRaycastScratch.Clear();
        es.RaycastAll(ped, _uiRaycastScratch);
        for (var i = 0; i < _uiRaycastScratch.Count; i++)
            if (_uiRaycastScratch[i].sortingOrder >= InteractiveLayerMinOrder) return true;
        return false;
    }

    // True when the pointer is over the layout toolbar specifically. Narrower than IsPointerOverUgui on purpose:
    // that one is true over ANY interactive mod window (>= 32755), which would wrongly block selecting those
    // windows in edit mode; this one fires only for the toolbar, so a press on the bar's buttons / empty drag
    // area is fully consumed by it (no eye-toggle, no window-select, no native-grab underneath). Reuses the
    // per-press raycast scratch buffer.
    //
    // Identifies the toolbar by HIERARCHY, not by sorting-order number, on purpose — do NOT "optimize" this
    // back to a `sortingOrder >= ToolbarOverrideSortingOrder` filter. The toolbar draws on a NESTED
    // overrideSorting Canvas (WindowSpec.OverrideSortingOrder = 32759, applied in WindowRenderer.Mount), but
    // RaycastResult.sortingOrder for a graphic on a nested overrideSorting canvas does NOT reliably report that
    // nested order — it reports the shared parent window canvas's order (32755). So a sorting-order threshold
    // never matched the toolbar's own hit, IsPointerOverToolbar returned false, and a plain click on the bar
    // still fell through to TrySelectWindow and selected the element underneath. Walking up the hit's parents
    // for the toolbar window's GameObject name is robust regardless of what order the nested canvas reports.
    // The toolbar root is created via UGuiPrimitives.NewRect(spec.Id, ...), so its GameObject name equals
    // WindowBuilder.LayoutToolbarWindowId.
    private bool IsPointerOverToolbar()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es == null) return false;
        var (px, py) = _input.PointerPosition;
        // PointerPosition is top-left/y-down; RaycastAll wants y-up → flip Y (X is unchanged).
        var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = new Vector2(px, UnityEngine.Screen.height - py) };
        _uiRaycastScratch.Clear();
        es.RaycastAll(ped, _uiRaycastScratch);
        for (var i = 0; i < _uiRaycastScratch.Count; i++)
        {
            var go = _uiRaycastScratch[i].gameObject;
            if (go == null) continue;
            for (var t = go.transform; t != null; t = t.parent)
                if (t.name == Stellar.Infrastructure.Game.WindowBuilder.LayoutToolbarWindowId) return true;
        }
        return false;
    }

    // True when the pointer is over an OPEN dropdown popup or its full-screen blocker. Same hierarchy-walk raycast
    // as IsPointerOverToolbar (and for the same reason — the popup's nested override-sorting canvas's order is NOT
    // reported by RaycastResult, so match by GameObject NAME, never by a sorting-order threshold). The popup +
    // blocker are canvas-root children named by WindowBuilder; while one is open the full-screen blocker is hit by
    // every press, so this returns true for ANY press during an open dropdown — exactly the intent (every such
    // press is a pick or a dismiss, never an editor select/drag). Reuses the per-press raycast scratch buffer.
    private bool IsPointerOverDropdownPopup()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es == null) return false;
        var (px, py) = _input.PointerPosition;
        // PointerPosition is top-left/y-down; RaycastAll wants y-up → flip Y (X is unchanged).
        var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = new Vector2(px, UnityEngine.Screen.height - py) };
        _uiRaycastScratch.Clear();
        es.RaycastAll(ped, _uiRaycastScratch);
        for (var i = 0; i < _uiRaycastScratch.Count; i++)
        {
            var go = _uiRaycastScratch[i].gameObject;
            if (go == null) continue;
            for (var t = go.transform; t != null; t = t.parent)
                if (t.name == Stellar.Infrastructure.Game.WindowBuilder.DropdownPanelName ||
                    t.name == Stellar.Infrastructure.Game.WindowBuilder.DropdownBlockerName) return true;
        }
        return false;
    }

    private void HandlePressed()
    {
        var (px, py) = _input.PointerPosition;
        // Toolbar first: it now renders (and hit-tests) ABOVE the edit chrome via its override-sorting canvas and
        // can be dragged over any element. A press on it belongs to the toolbar's own buttons / whole-frame drag —
        // never toggle an element's eye, select a window, or grab a native HUD element underneath it. Uses the
        // toolbar's EXACT override order (not the broad IsPointerOverUgui, which is also true over other mod windows
        // and would wrongly block selecting THEM).
        if (IsPointerOverToolbar()) return;
        // An OPEN dropdown popup (the toolbar's filter list) + its full-screen blocker are parented to the CANVAS
        // ROOT, not under the toolbar GameObject, so IsPointerOverToolbar() above does NOT catch them. A press to
        // pick an option (over the list) or to dismiss (over the blocker) must likewise be fully consumed here —
        // otherwise the SAME press also falls through to TrySelectWindow / the native-grab and selects/drags the
        // element underneath the open list.
        if (IsPointerOverDropdownPopup()) return;
        // Eye icon first: clicking a component's eye toggles its visibility (and consumes the press — no
        // select/drag). The chrome owns the eye geometry (raycaster-less canvas → manual rect hit-test).
        if (_chrome.TryGetEyeHit(px, py, out var eyeId)) { ToggleVisibility(eyeId); return; }

        // Editor-managed mod windows (CombatMeter etc.) ARE uGUI raycast targets and are dragged by the
        // WindowInteractionTicker, not this editor. But the ticker doesn't know about selection — so the editor
        // still has to SELECT the window under the press (highlight it yellow + make "Reset selected" target it).
        // Done before the IsPointerOverUgui gate precisely because the window is a raycast target.
        if (ShouldOutlineStellar && TrySelectWindow(px, py)) return;

        // A mod window is being dragged this press (claimed by the WindowInteractionTicker) — don't ALSO grab a
        // native/HUD element under it, which would move two things at once ("dragging moves two").
        if (Stellar.Infrastructure.Unity.EditDragArbiter.WindowDragActive) return;

        // Skip the grab when the click is over a uGUI raycast target — the toolbar window (and any draggable
        // uGUI plugin window) consume their own clicks; native game UI isn't a raycast target so stays grabbable.
        if (IsPointerOverUgui()) return;

        if (ShouldOutlineGameUi && TryGrabNativeUiEntry(px, py)) Stellar.Infrastructure.Unity.EditDragArbiter.EditorDragActive = true;
    }

    // Toggle one element's visibility, routing to whichever service owns the id (native → HUD → window). Reads
    // the current state from that service and flips it. No-op for an id no service owns.
    private void ToggleVisibility(string id)
    {
        if (_nativeUi is not null)
            foreach (var el in _nativeUi.EditableElements())
                if (el.Id == id) { _nativeUi.SetVisible(id, !el.Visible); return; }
        if (_windows is not null)
            foreach (var el in _windows.EditableElements())
                if (el.Id == id) { _windows.SetVisiblePersist(id, !el.Visible); return; }
    }

    // Select (don't grab) the editor-managed window under the pointer: the WindowInteractionTicker owns the
    // actual drag (and its on-screen clamp), so the editor only updates selection so the outline turns yellow
    // and "Reset selected" acts on the clicked window instead of the last-selected HUD/native element.
    private bool TrySelectWindow(float px, float py)
    {
        if (_windows is null) return false;
        foreach (var (id, rect) in _windows.EditableRects())
        {
            if (!WindowOwnerMatchesFilter(id)) continue;   // per-plugin filter: don't select another plugin's window
            if (!rect.Contains(px, py)) continue;
            _editor.SelectWindow(id);
            return true;
        }
        return false;
    }

    private bool TryGrabNativeUiEntry(float px, float py)
    {
        if (_nativeUi is null) return false;
        foreach (var e in _nativeUi.Entries)
        {
            if (!e.IsResolved || !e.Visible) continue;
            var rect = _nativeUi.GetLiveRect(e);   // current curated rect (matches the live outline)
            if (!rect.Contains(px, py)) continue;
            _editor.SelectWindow(e.Descriptor.Id);
            _editor.BeginDrag(px, py, rect);
            _dragKind = DragKind.NativeUi;
            return true;
        }
        return false;
    }

    private void HandleDragMove()
    {
        if (_editor.SelectedWindowId is not { } selected) return;
        var (px, py) = _input.PointerPosition;
        var res = _input.CurrentResolution;
        var others = BuildOtherWindowsList(selected);
        var newRect = _editor.UpdateDrag(px, py, others, res.Width, res.Height);

        if (_dragKind == DragKind.NativeUi)
            _nativeUi?.SetRect(selected, newRect);
    }

    private void HandleDragRelease()
    {
        Stellar.Infrastructure.Unity.EditDragArbiter.EditorDragActive = false;   // release the press claim (anti "moves two")
        if (_editor.SelectedWindowId is not { } sel) { _dragKind = DragKind.None; return; }
        if (_dragKind == DragKind.NativeUi && _nativeUi is not null)
        {
            foreach (var e in _nativeUi.Entries)
                if (e.Descriptor.Id == sel) { _editor.EndDrag(e.Rect, _input.CurrentResolution); break; }
            // Persist only on release (mod-window parity) — SetRect during the
            // drag is in-memory only, so we don't disk-write every frame.
            _nativeUi.Commit(sel);
        }
        _dragKind = DragKind.None;
    }

    // Reused snap-candidate buffer. HandleDragMove rebuilds it every frame
    // while the user is dragging; keeping a single List<WindowRect> field avoids
    // per-frame GC churn. The legacy IMGUI windows are gone, so the only snap
    // candidates left are HUDs/native-UI (added by their own paths); this
    // returns the empty buffer.
    private readonly List<WindowRect> _otherWindowsScratch = new(8);

    private List<WindowRect> BuildOtherWindowsList(string excludeId)
    {
        _otherWindowsScratch.Clear();
        return _otherWindowsScratch;
    }

}
