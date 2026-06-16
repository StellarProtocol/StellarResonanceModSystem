using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Per-frame interaction driver for the uGUI window toolkit, living on the Stellar window canvas
/// (<c>WindowRenderer</c> registers it). Two jobs, both via legacy-Input polling (the spike confirmed
/// legacy <c>Input</c> + standard EventSystem are active — avoids IL2CPP IDragHandler interface injection):
///  1. ticks each registered <see cref="UGuiTextInput"/> (cursor/Esc/debounce);
///  2. drives ColorPicker SV-square / hue-bar DRAG — on mouse-down over a registered area, and while held,
///     reports the normalised (x,y) to that area's pick callback (so dragging outside the rect still tracks).
/// Builder stays sandbox-pure: it registers fields/areas via callbacks; this IL2CPP MonoBehaviour is never
/// symlinked into the sandbox (mirrors <c>HudBarAnimator</c>).
/// </summary>
public sealed partial class WindowInteractionTicker : MonoBehaviour
{
    // Required by Il2CppInterop for managed MonoBehaviour subclasses.
    public WindowInteractionTicker(IntPtr ptr) : base(ptr) { }

    internal readonly List<UGuiTextInput> Fields = new();
    internal readonly List<(RectTransform Area, Action<float, float> Pick)> DragAreas = new();
    internal readonly List<(RectTransform Handle, RectTransform Target, bool EditOnly)> DragWindows = new();
    internal readonly List<(RectTransform Grip, RectTransform Target, Vector2 Min, Vector2 Max)> DragResizers = new();
    internal readonly List<(RectTransform Cell, Action<bool> SetHover)> Hovers = new();
    internal readonly List<Action<float>> Pulses = new();   // brand-logo glow pulse (driven per frame below)
    // Drag-to-rearrange cells (CombatMeter raid grid). Each is both a drag source and a drop target; CanDrag
    // gates the interaction (leader-only), Hover(on) toggles the cell's drop-target highlight. DragSlotDrop is
    // shared across one grid's cells (set per build) and fires (fromKey, toKey) on a valid drop.
    internal readonly List<(RectTransform Cell, int Key, Func<bool> CanDrag, Action<bool> Hover)> DragSlots = new();
    internal Action<int, int>? DragSlotDrop;
    // Right-click cells (CombatMeter row context menu). Right-button-down over a cell fires its callback.
    internal readonly List<(RectTransform Cell, Action OnRightClick)> RightClicks = new();
    // Live render-texture hosts (e.g. the inspector 3D portrait): each frame, pull the boxed Texture and bind it
    // onto the RawImage. Optional Drag (orbit) / Zoom (scroll) / Pan (shift+drag) callbacks make it interactive.
    internal readonly List<(RawImage Img, Func<object?> Texture, Action<float, float>? Drag, Action<float>? Zoom, Action<float, float>? Pan, Action<int, int>? Resize)> RenderHosts = new();
    // Game-asset icon hosts (GameTextureElement: profession crests / imagine icons): each frame, pull the boxed
    // texture and bind it onto the RawImage (hidden while null — async loads land late), plus the optional
    // dynamic UV sub-rect for atlas sprites. The non-interactive sibling of RenderHosts. `Bound` caches the
    // last managed handle so the per-frame diff never reads the interop texture getter back.
    internal struct IconHost
    {
        public RawImage Img; public Func<object?> Texture; public Func<UvRect>? Uv; public Texture? Bound;
        // Letterbox target: the RawImage's centred RectTransform is resized to the true texel aspect
        // (UV sub-rect × texture size) within the BoxW×BoxH layout box on bind, so sprites never
        // stretch to the box shape. Manual math, not AspectRatioFitter (IL2CPP stripping risk).
        public float BoxW, BoxH;
    }
    internal readonly List<IconHost> IconHosts = new();
    // Scrollbar rects: drag-exclusion zones. The manual window-drag hit-test must yield to the uGUI
    // Scrollbar (EventSystem-driven) — pressing the bar on a whole-frame-draggable window (Party chrome,
    // e.g. the entity inspector) dragged the window instead of scrolling (user-flagged 2026-06-13).
    internal readonly List<RectTransform> ScrollbarRects = new();
    private int _activeRenderHost = -1;
    private readonly List<bool> _hoverState = new();
    private const float PulseSpeed = 2.6f;                  // radians/sec — matches the old IMGUI GlowPulseSpeed
    private int _activeDrag = -1;
    private RectTransform? _activeWinDrag;
    private int _activeResize = -1;
    private int _activeSlotDrag = -1;          // index into DragSlots of the grabbed cell, or -1
    private int _activeSlotKey;                // the grabbed cell's Key
    private int _hoverSlotKey = int.MinValue;  // last cell we told Hover(true) about
    private GameObject? _ghost;                // cursor-following drag ghost
    private RectTransform? _ghostRt;           // cached ghost transform (avoids per-held-frame GetComponent)
    private Vector2 _lastMouse;
    private int _throwLogged;

    internal void Prune()
    {
        for (var i = DragAreas.Count - 1; i >= 0; i--) if (DragAreas[i].Area == null) DragAreas.RemoveAt(i);
        for (var i = DragWindows.Count - 1; i >= 0; i--) if (DragWindows[i].Handle == null || DragWindows[i].Target == null) DragWindows.RemoveAt(i);
        for (var i = DragResizers.Count - 1; i >= 0; i--) if (DragResizers[i].Grip == null || DragResizers[i].Target == null) DragResizers.RemoveAt(i);
        for (var i = Hovers.Count - 1; i >= 0; i--) if (Hovers[i].Cell == null) { Hovers.RemoveAt(i); if (i < _hoverState.Count) _hoverState.RemoveAt(i); }
        for (var i = DragSlots.Count - 1; i >= 0; i--) if (DragSlots[i].Cell == null) DragSlots.RemoveAt(i);
        for (var i = RightClicks.Count - 1; i >= 0; i--) if (RightClicks[i].Cell == null) RightClicks.RemoveAt(i);
        for (var i = RenderHosts.Count - 1; i >= 0; i--) if (RenderHosts[i].Img == null) RenderHosts.RemoveAt(i);
        for (var i = IconHosts.Count - 1; i >= 0; i--) if (IconHosts[i].Img == null) IconHosts.RemoveAt(i);
        for (var i = ScrollbarRects.Count - 1; i >= 0; i--) if (ScrollbarRects[i] == null) ScrollbarRects.RemoveAt(i);
        PruneChartPans();
        if (_activeSlotDrag >= 0 && (_activeSlotDrag >= DragSlots.Count || DragSlots[_activeSlotDrag].Cell == null))
            EndSlotDrag(commit: false);
    }

    // Tile hover (icon grow + brighten) + brand-logo glow pulse. Hover polls the pointer against each registered
    // cell (skipping the launcher's pool of hidden tiles); fires on change only.
    private void TickHoversAndPulses()
    {
        var hp = Input.mousePosition;
        for (var i = 0; i < Hovers.Count; i++)
        {
            while (_hoverState.Count <= i) _hoverState.Add(false);
            var (cell, set) = Hovers[i];
            if (cell == null) continue;
            if (!cell.gameObject.activeInHierarchy) { if (_hoverState[i]) { _hoverState[i] = false; try { set(false); } catch { } } continue; }
            var over = RectTransformUtility.RectangleContainsScreenPoint(cell, hp, null);
            if (over != _hoverState[i]) { _hoverState[i] = over; try { set(over); } catch { } }
        }

        if (Pulses.Count > 0)
        {
            var pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * PulseSpeed);
            for (var i = 0; i < Pulses.Count; i++) { try { Pulses[i](pulse); } catch { } }
        }
    }

    private void Update()
    {
        for (var i = 0; i < Fields.Count; i++)
        {
            try { Fields[i].Tick(); }
            catch { if (_throwLogged++ == 0) Debug.LogWarning("[Window] field tick threw (rate-limited)"); }
        }

        TickHoversAndPulses();
        TickRenderHosts();
        TickIconHosts();

        if (Input.GetMouseButtonDown(0)) BeginPointerDrag();
        if (Input.GetMouseButton(0)) TickActivePointer();
        else ReleasePointer();

        if (Input.GetMouseButtonDown(1)) TickRightClick();
        TickRenderHostZoom();
        TickChartZoom();   // .ChartPan.cs
    }

    // Scroll over a render-host box → zoom callback (e.g. the portrait camera). Scoped to the box rect so it
    // doesn't fight the attribute-list scroll wheel (the list is a separate region).
    private void TickRenderHostZoom()
    {
        var scroll = Input.mouseScrollDelta.y;
        if (scroll == 0f) return;
        var mp = Input.mousePosition;
        for (var i = 0; i < RenderHosts.Count; i++)
        {
            var (img, _, _, zoom, _, _) = RenderHosts[i];
            if (img == null || zoom == null || !img.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(img.rectTransform, mp, null)) continue;
            try { zoom(scroll); } catch { }
            return;
        }
    }

    // Bind each render-texture host's current texture onto its RawImage (cheap; the texture may appear late).
    private void TickRenderHosts()
    {
        for (var i = 0; i < RenderHosts.Count; i++)
        {
            var (img, fn, _, _, _, resize) = RenderHosts[i];
            if (img == null) continue;
            try
            {
                // Report the box's current pixel size FIRST so the preview resizes its RT before we bind it —
                // binding the old RT then destroying it (in resize) showed a destroyed texture for a frame (the
                // white resize flicker). The preview fills the pane top-to-bottom with no letterbox or stretch.
                if (resize != null)
                {
                    var r = img.rectTransform.rect;
                    if (r.width >= 1f && r.height >= 1f) resize((int)r.width, (int)r.height);
                }
                // Hide the RawImage while its texture is null (the model is still loading) — an enabled
                // RawImage with no texture draws uGUI's default SOLID WHITE, which flashed the pane white on
                // every open until the model landed. With it hidden, the dark RenderHostBg backdrop shows
                // instead. Same guard the icon-host path (TickIconHosts) already applies.
                var tex = fn() as Texture;
                img.texture = tex;
                if (img.enabled != (tex != null)) img.enabled = tex != null;
            }
            catch { }
        }
    }

    // Bind each icon host's current game-asset texture onto its RawImage (cheap; async loads land late).
    // The image stays hidden while the texture is null so no white placeholder box flashes, and the optional
    // UV func re-points the RawImage at the right atlas cell (value-diffed — assignment dirties the canvas).
    // Hosts inside a hidden window/tab are skipped entirely; a late-loaded icon binds on its first active frame.
    private void TickIconHosts()
    {
        for (var i = 0; i < IconHosts.Count; i++)
        {
            var h = IconHosts[i];
            if (h.Img == null || !h.Img.gameObject.activeInHierarchy) continue;
            try
            {
                var tex = h.Texture() as Texture;
                // Resolve the UV BEFORE binding/enabling so a throwing Uv func fails closed (icon stays
                // hidden) instead of showing the whole atlas sheet at the default 0,0,1,1 rect.
                var uvChanged = false;
                if (tex != null && h.Uv != null)
                {
                    var r = h.Uv();
                    var rect = new Rect(r.X, r.Y, r.W, r.H);
                    if (h.Img.uvRect != rect) { h.Img.uvRect = rect; uvChanged = true; }
                }
                if (!ReferenceEquals(h.Bound, tex))
                {
                    h.Img.texture = tex;
                    h.Img.enabled = tex != null;
                    h.Bound = tex;
                    IconHosts[i] = h;
                    uvChanged = tex != null;   // (re)bound — refresh the letterbox aspect too
                }
                if (uvChanged && tex != null) UpdateIconAspect(h, tex);
            }
            catch { if (_throwLogged++ == 0) Debug.LogWarning("[Window] icon-host tick threw (rate-limited)"); }
        }
    }

    // True displayed aspect = UV sub-rect × texture size (atlas cells are rarely the box shape).
    // Only called on bind / UV change — never per steady-state frame (tex.width crosses interop).
    private static void UpdateIconAspect(in IconHost h, Texture tex)
    {
        if (h.BoxW <= 0f || h.BoxH <= 0f) return;
        var uv = h.Img.uvRect;
        float tw = uv.width * tex.width, th = uv.height * tex.height;
        if (tw <= 0f || th <= 0f) return;
        var texAspect = tw / th;
        float w, ht;
        if (texAspect > h.BoxW / h.BoxH) { w = h.BoxW; ht = h.BoxW / texAspect; }
        else                             { ht = h.BoxH; w = h.BoxH * texAspect; }
        h.Img.rectTransform.sizeDelta = new Vector2(w, ht);
    }

    // Right-button-down over a registered cell fires its context-menu callback (topmost-registered wins).
    private void TickRightClick()
    {
        var mp = Input.mousePosition;
        for (var i = 0; i < RightClicks.Count; i++)
        {
            var (cell, cb) = RightClicks[i];
            if (cell == null || !cell.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(cell, mp, null)) continue;
            try { cb(); } catch { if (_throwLogged++ == 0) Debug.LogWarning("[Window] right-click cb threw (rate-limited)"); }
            return;
        }
    }

    // Mouse-down: pick what the press grabs, in priority order. A draggable card claims the pointer first, so
    // grabbing a card never also drags the window; then color-picker areas, resize grips, then window handles.
    private void BeginPointerDrag()
    {
        // Render-host (3D portrait) drag claims the pointer first so dragging the box orbits its camera.
        _activeRenderHost = HitRenderHost(Input.mousePosition);
        if (_activeRenderHost >= 0) { _lastMouse = Input.mousePosition; return; }
        // Chart plot drag pans its visible window — claim before slot/window drags so dragging the plot
        // never also moves the window beneath it. No EditDragArbiter.WindowDragActive is set for chart-pan: it
        // moves no transform (only the plugin-owned visible range), so there's nothing for the layout editor to
        // arbitrate against — hence the asymmetry with the slot/resize/window-drag paths below.
        _activeChartPan = HitChartPan(Input.mousePosition);
        if (_activeChartPan >= 0) { _lastMouse = Input.mousePosition; return; }
        _activeSlotDrag = HitSlot(Input.mousePosition);
        if (_activeSlotDrag >= 0) BeginSlotDrag(_activeSlotDrag);
        _activeDrag = _activeSlotDrag < 0 ? HitTest(Input.mousePosition) : -1;
        _activeResize = _activeSlotDrag < 0 && _activeDrag < 0 ? HitResizeGrip(Input.mousePosition) : -1;   // grip before titlebar
        // A press on a scrollbar belongs to the uGUI Scrollbar (EventSystem) — never start a window drag there.
        // Also yield if the layout editor already claimed this press for a native/HUD element (anti "moves two").
        _activeWinDrag = _activeSlotDrag < 0 && _activeDrag < 0 && _activeResize < 0
            && !PointerOnScrollbar(Input.mousePosition) && !EditDragArbiter.EditorDragActive
            ? HitWindowHandle(Input.mousePosition) : null;
        EditDragArbiter.WindowDragActive = _activeWinDrag != null || _activeSlotDrag >= 0 || _activeResize >= 0;
        _lastMouse = Input.mousePosition;
    }

    // Mouse-held: drive whichever single interaction was claimed on mouse-down.
    private void TickActivePointer()
    {
        if (_activeRenderHost >= 0) { TickRenderHostDrag(); return; }
        if (_activeChartPan >= 0) { TickChartPanDrag(); return; }
        if (_activeSlotDrag >= 0) { TickSlotDrag(); return; }
        if (_activeResize >= 0 && _activeResize < DragResizers.Count) { TickResize(); return; }
        if (_activeDrag >= 0 && _activeDrag < DragAreas.Count) { TickPickArea(); return; }
        if (_activeWinDrag != null)
        {
            var m = (Vector2)Input.mousePosition;
            _activeWinDrag.anchoredPosition += m - _lastMouse;   // top-left anchor: screen delta maps 1:1
            ClampWinDragOnScreen();                              // never let a drag fling the window off-screen (lost forever)
            _lastMouse = m;
        }
    }

    // Keep the dragged window's grabbable region on-screen. anchoredPosition is (X, -Y) with a top-left anchor
    // (WindowRenderer's convention), so we round-trip through a WindowRect and the SAME clamp the persistence
    // layer uses (LayoutStorage.ClampVisible) — so the live drag and the restored rect agree, and a window can
    // never be dragged fully off-screen (the bug where the CombatMeter vanished and never returned).
    private void ClampWinDragOnScreen()
    {
        if (_activeWinDrag == null) return;
        var ap = _activeWinDrag.anchoredPosition;
        var size = _activeWinDrag.rect.size;
        var rect = new Stellar.Abstractions.Domain.WindowRect(ap.x, -ap.y, size.x, size.y);
        var clamped = Stellar.Application.Services.LayoutStorage.ClampVisible(
            rect, new Stellar.Abstractions.Domain.Resolution(Screen.width, Screen.height));
        _activeWinDrag.anchoredPosition = new Vector2(clamped.X, -clamped.Y);
    }

    private void ReleasePointer()
    {
        if (_activeSlotDrag >= 0) EndSlotDrag(commit: true);
        _activeDrag = -1; _activeWinDrag = null; _activeResize = -1; _activeRenderHost = -1; _activeChartPan = -1;
        EditDragArbiter.WindowDragActive = false;
    }

    // Drag over a render-host box → orbit its camera; with Shift held → pan instead (move the camera).
    private void TickRenderHostDrag()
    {
        if (_activeRenderHost >= RenderHosts.Count) { _activeRenderHost = -1; return; }
        var host = RenderHosts[_activeRenderHost];
        var m = (Vector2)Input.mousePosition;
        var d = m - _lastMouse;
        _lastMouse = m;
        var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        try
        {
            if (shift && host.Pan != null) host.Pan(d.x, d.y);
            else host.Drag?.Invoke(d.x, d.y);
        }
        catch { }
    }

    private int HitRenderHost(Vector3 mp)
    {
        for (var i = 0; i < RenderHosts.Count; i++)
        {
            var (img, _, drag, _, pan, _) = RenderHosts[i];
            if (img == null || (drag == null && pan == null) || !img.gameObject.activeInHierarchy) continue;
            if (RectTransformUtility.RectangleContainsScreenPoint(img.rectTransform, mp, null)) return i;
        }
        return -1;
    }

    private void TickSlotDrag()
    {
        if (_ghostRt != null) _ghostRt.position = Input.mousePosition;
        var over = HitDropTarget(Input.mousePosition);
        var key = over >= 0 ? DragSlots[over].Key : int.MinValue;
        if (key != _hoverSlotKey) { SetHover(_hoverSlotKey, false); SetHover(key, true); _hoverSlotKey = key; }
    }

    private void TickResize()
    {
        var (_, target, min, max) = DragResizers[_activeResize];
        if (target == null) return;
        var m = (Vector2)Input.mousePosition;
        var d = m - _lastMouse;   // grow right with +x; grow down (screen y decreases) with -y
        var s = target.sizeDelta;
        target.sizeDelta = new Vector2(
            Mathf.Clamp(s.x + d.x, min.x, max.x),
            Mathf.Clamp(s.y - d.y, min.y, max.y));
        _lastMouse = m;
    }

    private void TickPickArea()
    {
        var (area, pick) = DragAreas[_activeDrag];
        if (area == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(area, Input.mousePosition, null, out var lp)) return;
        var r = area.rect;
        try { pick(Mathf.Clamp01((lp.x - r.x) / r.width), Mathf.Clamp01((lp.y - r.y) / r.height)); }
        catch { if (_throwLogged++ == 0) Debug.LogWarning("[Window] picker pick threw (rate-limited)"); }
    }

    // SOURCE hit-test (mouse-down): the cell must be active AND pass CanDrag — so a drag only STARTS from a
    // draggable, occupied slot (CanDrag gates leader/Raid20/occupancy).
    private int HitSlot(Vector3 mp)
    {
        for (var i = 0; i < DragSlots.Count; i++)
        {
            var (cell, _, canDrag, _) = DragSlots[i];
            if (cell == null || !cell.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(cell, mp, null)) continue;
            try { if (canDrag != null && !canDrag()) continue; } catch { continue; }
            return i;
        }
        return -1;
    }

    // TARGET hit-test (hover + drop): any active registered cell under the cursor — NOT gated on CanDrag, so an
    // EMPTY slot is a valid drop destination (CanDrag's occupancy check only restricts the source). A drag is
    // already in progress (started from a CanDrag source), so every active cell in that grid is a legal target.
    private int HitDropTarget(Vector3 mp)
    {
        for (var i = 0; i < DragSlots.Count; i++)
        {
            var cell = DragSlots[i].Cell;
            if (cell == null || !cell.gameObject.activeInHierarchy) continue;
            if (RectTransformUtility.RectangleContainsScreenPoint(cell, mp, null)) return i;
        }
        return -1;
    }

    private void BeginSlotDrag(int idx)
    {
        _activeSlotKey = DragSlots[idx].Key;
        _hoverSlotKey = int.MinValue;
        var src = DragSlots[idx].Cell;
        if (src == null) return;
        // Ghost = a dimmed, non-interactive copy of the cell, parented to the canvas root at top sort order.
        var parent = src.GetComponentInParent<Canvas>()?.transform ?? src.root;
        _ghost = UnityEngine.Object.Instantiate(src.gameObject, parent);
        _ghost.name = "DragGhost";
        _ghostRt = _ghost.GetComponent<RectTransform>();
        _ghostRt.sizeDelta = src.rect.size;
        _ghostRt.position = Input.mousePosition;
        _ghostRt.SetAsLastSibling();
        var cg = _ghost.GetComponent<CanvasGroup>() ?? _ghost.AddComponent<CanvasGroup>();
        cg.alpha = 0.7f; cg.blocksRaycasts = false; cg.interactable = false;
    }

    private void EndSlotDrag(bool commit)
    {
        SetHover(_hoverSlotKey, false);
        if (commit && _ghost != null)
        {
            var over = HitDropTarget(Input.mousePosition);
            if (over >= 0 && DragSlots[over].Key != _activeSlotKey)
            {
                try { DragSlotDrop?.Invoke(_activeSlotKey, DragSlots[over].Key); }
                catch { if (_throwLogged++ == 0) Debug.LogWarning("[Window] slot-drop threw (rate-limited)"); }
            }
        }
        if (_ghost != null) { UnityEngine.Object.Destroy(_ghost); _ghost = null; }
        _ghostRt = null;
        _activeSlotDrag = -1; _hoverSlotKey = int.MinValue;
    }

    // Skip inactive cells: a ConditionalElement builds both branches, so the same Key can be registered twice
    // (active 4-group quadrant + hidden single-column branch). Mirror HitSlot's activeInHierarchy guard so the
    // highlight never lands on the hidden twin.
    private void SetHover(int key, bool on)
    {
        if (key == int.MinValue) return;
        for (var i = 0; i < DragSlots.Count; i++)
            if (DragSlots[i].Key == key && DragSlots[i].Cell != null && DragSlots[i].Cell.gameObject.activeInHierarchy)
            { try { DragSlots[i].Hover(on); } catch { } return; }
    }

    private int HitResizeGrip(Vector3 mp)
    {
        for (var i = 0; i < DragResizers.Count; i++)
            if (DragResizers[i].Grip != null && DragResizers[i].Grip.gameObject.activeInHierarchy
                && RectTransformUtility.RectangleContainsScreenPoint(DragResizers[i].Grip, mp, null))
                return i;
        return -1;
    }

    // Re-assert a free, visible cursor in LateUpdate (AFTER the game's per-frame cursor-hide in its Update) so
    // a focused Stellar field doesn't lose the cursor — the game hides the cursor in gameplay (Alt frees it),
    // but Alt is suppressed while a field is focused, so the framework must keep the cursor itself. Field.Tick
    // already sets it in Update; this wins the race against a later game-side hide.
    private void LateUpdate()
    {
        for (var i = 0; i < Fields.Count; i++)
        {
            if (Fields[i].IsFocused) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; return; }
        }
    }

    // The bar is only 5px wide — inflate the exclusion zone 6px each side so a slightly-off press still
    // reaches the Scrollbar instead of starting a window drag.
    private bool PointerOnScrollbar(Vector3 mp)
    {
        for (var i = 0; i < ScrollbarRects.Count; i++)
        {
            var rt = ScrollbarRects[i];
            if (rt == null || !rt.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, mp, null, out var lp)) continue;
            var r = rt.rect;
            if (lp.x >= r.xMin - 6f && lp.x <= r.xMax + 6f && lp.y >= r.yMin && lp.y <= r.yMax) return true;
        }
        return false;
    }

    private RectTransform? HitWindowHandle(Vector3 mp)
    {
        for (var i = 0; i < DragWindows.Count; i++)
        {
            // Edit-only windows (overlay/status chromes) drag only while layout edit-mode is active — so they
            // don't move accidentally during play. Popup dialogs (EditOnly=false) drag any time.
            if (DragWindows[i].EditOnly && !LayoutEditGate.IsEditing) continue;
            if (DragWindows[i].Handle != null && RectTransformUtility.RectangleContainsScreenPoint(DragWindows[i].Handle, mp, null))
                return DragWindows[i].Target;
        }
        return null;
    }

    private int HitTest(Vector3 mp)
    {
        for (var i = 0; i < DragAreas.Count; i++)
            if (DragAreas[i].Area != null && RectTransformUtility.RectangleContainsScreenPoint(DragAreas[i].Area, mp, null))
                return i;
        return -1;
    }
}
