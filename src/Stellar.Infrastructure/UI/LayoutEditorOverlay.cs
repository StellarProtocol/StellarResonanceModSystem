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
/// windows + HUDs + native game UI) when <see cref="LayoutEditorService.IsEditing"/>
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
/// buttons), [Reset selected] + [Reset all] buttons, and a "Shift+` to exit"
/// hint. Toolbar rendering lives in <c>LayoutEditorOverlay.Toolbar.cs</c>.
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
    private NativeUiService? _nativeUi;   // bound late via SetNativeUi (Phase 9a)
    private HudService? _hud;             // bound late via SetHud (Task 6)

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
    private readonly UGuiInputBlocker _editInputBlocker = new(EditBlockerSortingOrder);

    public LayoutEditorOverlay(LayoutEditorService editor, IInputGateway input,
                                LayoutStorage storage, ITheme theme, IPluginLog log)
    {
        _editor = editor;
        _input = input;
        _storage = storage;
        _theme = theme;
        _log = log;
    }

    /// <summary>True while layout edit-mode is active — its chrome draws through OnGUI,
    /// so the host counts it as live IMGUI content for the overlay lifecycle.</summary>
    public bool IsEditing => _editor.IsEditing;

    /// <summary>Phase 9a: bind the native-UI service so Shift+` also outlines + drags game HUD elements.</summary>
    public void SetNativeUi(NativeUiService nativeUi) => _nativeUi = nativeUi;

    /// <summary>Task 6: bind the uGUI HUD service so edit-mode can manage HUD elements.</summary>
    public void SetHud(HudService hud) => _hud = hud;

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
        if (_editor.IsEditing)
        {
            if (!_wasEditing) { ShowToolbar(); _editInputBlocker.SetActive(true); _nativeUi?.DumpRectDiagnostics(_log.Info); }   // bug #5 evidence (self-gates on diagnostics)
            ProcessInput();
            SyncChrome();   // build the editable-element list + push to the uGUI overlay (EnsureCanvas self-heals)
        }
        else if (_wasEditing)
        {
            _chrome.Teardown();   // destroy the overlay canvas on edit-mode exit
            HideToolbar();
            _editInputBlocker.SetActive(false);   // stop absorbing game-UI clicks once out of edit mode
            Stellar.Infrastructure.Unity.EditDragArbiter.EditorDragActive = false;
        }
        _wasEditing = _editor.IsEditing;
    }

    // Build the live editable-element list (windows + HUDs + native game-UI, respecting filters + selection)
    // and push it to the uGUI overlay. Runs each tick AFTER ProcessInput so outlines track an in-progress drag.
    private void SyncChrome()
    {
        _chromeItems.Clear();
        if (ShouldOutlineStellar && _windows != null) AddWindowServiceItems(_chromeItems);
        if (_hud != null) AddHudItems(_chromeItems);
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
            var colour = _editor.SelectedWindowId == el.Id ? OutlineSelected : OutlineUnselected;
            items.Add(new Stellar.Infrastructure.Game.EditChromeItem(el.Rect, colour, el.Id, el.Id, el.Visible, el.CanHide));
        }
    }

    /// <summary>Edit-mode chrome over each uGUI HUD (incl. hidden) so they're discoverable + grabbable + toggleable.</summary>
    private void AddHudItems(List<Stellar.Infrastructure.Game.EditChromeItem> items)
    {
        foreach (var el in _hud!.EditableElements())
        {
            var colour = _editor.SelectedWindowId == el.Id ? OutlineSelected : OutlineUnselected;
            items.Add(new Stellar.Infrastructure.Game.EditChromeItem(el.Rect, colour, el.Id, el.Id, el.Visible, el.CanHide));
        }
    }

    /// <summary>Source of the currently-dragged element — picks the commit path on mouse-up.</summary>
    private enum DragKind { None, NativeUi, Hud }
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
        var ped = new UnityEngine.EventSystems.PointerEventData(es) { position = new Vector2(px, py) };
        _uiRaycastScratch.Clear();
        es.RaycastAll(ped, _uiRaycastScratch);
        for (var i = 0; i < _uiRaycastScratch.Count; i++)
            if (_uiRaycastScratch[i].sortingOrder >= InteractiveLayerMinOrder) return true;
        return false;
    }

    private void HandlePressed()
    {
        var (px, py) = _input.PointerPosition;
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
        // uGUI plugin window) consume their own clicks; HUDs/IMGUI windows aren't raycast targets so stay grabbable.
        if (IsPointerOverUgui()) return;

        if (_hud != null && TryGrabHud(px, py)) { Stellar.Infrastructure.Unity.EditDragArbiter.EditorDragActive = true; return; }
        if (ShouldOutlineGameUi && TryGrabNativeUiEntry(px, py)) Stellar.Infrastructure.Unity.EditDragArbiter.EditorDragActive = true;
    }

    // Toggle one element's visibility, routing to whichever service owns the id (native → HUD → window). Reads
    // the current state from that service and flips it. No-op for an id no service owns.
    private void ToggleVisibility(string id)
    {
        if (_nativeUi is not null)
            foreach (var el in _nativeUi.EditableElements())
                if (el.Id == id) { _nativeUi.SetVisible(id, !el.Visible); return; }
        if (_hud is not null)
            foreach (var el in _hud.EditableElements())
                if (el.Id == id) { _hud.SetVisiblePersist(id, !el.Visible); return; }
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
            if (!rect.Contains(px, py)) continue;
            _editor.SelectWindow(id);
            return true;
        }
        return false;
    }

    private bool TryGrabHud(float px, float py)
    {
        foreach (var (id, rect) in _hud!.ShownRects())
        {
            if (!rect.Contains(px, py)) continue;
            _editor.SelectWindow(id);
            _editor.BeginDrag(px, py, rect);
            _hud.BeginDrag(id);
            _dragKind = DragKind.Hud;
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
        {
            _nativeUi?.SetRect(selected, newRect);
        }
        else if (_dragKind == DragKind.Hud)
        {
            _hud?.SetRect(selected, newRect);
        }
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
        else if (_dragKind == DragKind.Hud && _hud is not null)
        {
            // Persist only on release (mod-window parity); SetRect during the drag is in-memory.
            _editor.EndDrag(_hud.GetRect(sel), _input.CurrentResolution);
            _hud.CommitRect(sel);
            _hud.EndDrag(sel);
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
