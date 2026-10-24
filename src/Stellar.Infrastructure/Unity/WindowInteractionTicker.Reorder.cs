using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

// ListElement.OnReorder — drag-and-drop reordering of a list's items. Same legacy-Input polling model as the raid-grid
// slot drag above it (Input + geometric hit-tests, no IL2CPP IBeginDragHandler injection), driven from the shared
// BeginPointerDrag / TickActivePointer / ReleasePointer state machine so a press is claimed by exactly ONE gesture:
//  • press over a live item (FrontWindowBlocks-gated, inside the scroll viewport) → PENDING. The press is claimed
//    here, so BeginPointerDrag returns before the window-drag / resize / slot paths — the window can never move.
//    The enclosing ScrollRect's axes are switched off for the whole press so the EventSystem's own drag (which
//    bubbles from the item's Button to the ScrollRect) cannot scroll the grid under the cursor.
//  • moved past ReorderThresholdPx → DRAGGING: the source dims, a same-size ghost copy follows the cursor holding the
//    press-time grab offset (the grabbed point stays under the pointer), an accent bar marks
//    the insertion point. The real grid is NOT reflowed mid-drag (stable — the consumer reorders on drop).
//  • release → drop: insertion recomputed at the release point; OnReorder(from, to) with remove-then-insert `to`.
//    Released while still PENDING = an ordinary click (the Button's onClick fires as usual).
//  • Esc while held, or a release outside the list's viewport / under a front window → cancel.
// After a real drag, ReorderSuppressesClick swallows the click the EventSystem may still deliver on the release
// (drop back on the source cell inside its own drag threshold) — WindowBuilder.ClickSuppressed reads it.
public sealed partial class WindowInteractionTicker
{
    internal readonly List<WindowBuilder.ReorderListReg> ReorderLists = new();

    private const float ReorderThresholdPx = 6f;   // screen px of travel before a press becomes a drag
    private const float ReorderBarThickness = 3f;  // insertion bar, canvas units

    private WindowBuilder.ReorderListReg? _rl;     // the list the current press landed on (pending or dragging)
    private int _rlFrom = -1;                      // pressed item index
    private Vector2 _rlPress;                      // press point (screen px) for the threshold test
    private bool _rlDragging;
    private bool _rlSwallow;                       // a drag started during this press → swallow the release click
    private int _rlSuppressUntil = -1;             // frame up to which the post-release click stays swallowed
    private RectTransform? _rlZone;                // drop zone: the enclosing scroll viewport, else the list itself
    private ScrollRect? _rlScroll;
    private bool _rlScrollH, _rlScrollV;           // the ScrollRect's axes before the press locked them
    private GameObject? _rlGhost;
    private RectTransform? _rlGhostRt;
    private RectTransform? _rlGhostParent;         // the ghost's parent (nearest Canvas) — the grab offset's space
    private Vector2 _rlGrab;                       // ghost pivot − press point, in _rlGhostParent local units
    private RectTransform? _rlBarRt;
    private CanvasGroup? _rlSrcGroup;
    private float _rlSrcAlpha = 1f;

    // True while a reorder drag is held, and for the release frame + 1 (EventSystem/ticker Update order is undefined,
    // so the release click may land either side of ReleasePointer).
    internal bool ReorderSuppressesClick => _rlSwallow || Time.frameCount <= _rlSuppressUntil;

    private void PruneReorder()
    {
        for (var i = ReorderLists.Count - 1; i >= 0; i--) if (ReorderLists[i].List == null) ReorderLists.RemoveAt(i);
        if (_rl != null && _rl.List == null) EndReorder(commit: false);
    }

    private static int LiveCount(WindowBuilder.ReorderListReg reg)
    {
        int n;
        try { n = reg.Count(); } catch { return 0; }
        return Mathf.Clamp(n, 0, reg.Cells.Length);
    }

    // Mouse-down: claim the press if it lands on a live item of a reorderable list. Returns true when claimed.
    private bool BeginReorderPress(Vector3 mp)
    {
        for (var r = 0; r < ReorderLists.Count; r++)
        {
            var reg = ReorderLists[r];
            if (reg.List == null || !reg.List.gameObject.activeInHierarchy) continue;
            var n = LiveCount(reg);
            for (var i = 0; i < n; i++)
            {
                var cell = reg.Cells[i];
                if (cell == null || !cell.gameObject.activeInHierarchy) continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(cell, mp, null)) continue;
                if (FrontWindowBlocks(mp, FindWindowRoot(reg.List))) return false;   // a front window owns this press
                var scroll = reg.List.GetComponentInParent<ScrollRect>();
                var zone = scroll != null ? (scroll.viewport != null ? scroll.viewport : scroll.GetComponent<RectTransform>()) : reg.List;
                // An item scrolled out of view is masked, not hidden — a press there belongs to whatever is visible.
                if (!RectTransformUtility.RectangleContainsScreenPoint(zone, mp, null)) break;
                _rl = reg; _rlFrom = i; _rlPress = mp; _rlDragging = false; _rlZone = zone;
                LockScroll(scroll);
                return true;
            }
        }
        return false;
    }

    // Mouse-held while a reorder press is claimed.
    private void TickReorder()
    {
        var reg = _rl;
        if (reg == null) return;
        if (reg.List == null || !reg.List.gameObject.activeInHierarchy || _rlFrom >= LiveCount(reg)) { EndReorder(commit: false); return; }
        if (Input.GetKeyDown(KeyCode.Escape)) { EndReorder(commit: false); return; }   // _rlSwallow stays set until release
        Vector2 mp = Input.mousePosition;
        if (!_rlDragging)
        {
            if ((mp - _rlPress).sqrMagnitude < ReorderThresholdPx * ReorderThresholdPx) return;
            StartReorderDrag(reg);
        }
        PlaceGhost(mp);
        PlaceInsertionBar(reg, mp);
    }

    // Mouse-up (called every idle frame from ReleasePointer): drop or end the press, then arm the click swallow.
    private void ReleaseReorder()
    {
        if (_rl != null) EndReorder(commit: _rlDragging);
        if (_rlSwallow) { _rlSwallow = false; _rlSuppressUntil = Time.frameCount + 1; }
    }

    private void StartReorderDrag(WindowBuilder.ReorderListReg reg)
    {
        _rlDragging = true;
        _rlSwallow = true;
        var src = reg.Cells[_rlFrom];
        var parent = src.GetComponentInParent<Canvas>()?.transform ?? src.root;
        // Ghost first (cloned before the source is dimmed), as a non-interactive top-sibling copy that ignores any
        // layout group on its parent (an IsolateCanvas window root is itself the nearest Canvas).
        _rlGhost = Object.Instantiate(src.gameObject, parent);
        _rlGhost.name = "ReorderGhost";
        _rlGhostRt = _rlGhost.GetComponent<RectTransform>();
        IgnoreLayout(_rlGhost);
        // A fitter on the cell's root would re-size the clone from its content, overriding the explicit sizeDelta.
        var csf = _rlGhost.GetComponent<ContentSizeFitter>(); if (csf != null) csf.enabled = false;
        var arf = _rlGhost.GetComponent<AspectRatioFitter>(); if (arf != null) arf.enabled = false;
        _rlGhostParent = parent as RectTransform;
        MatchGhostToSource(src);
        PlaceGhost(Input.mousePosition);   // same frame as the Instantiate → never rendered at the clone's old spot
        var gcg = _rlGhost.GetComponent<CanvasGroup>();
        if (gcg == null) gcg = _rlGhost.AddComponent<CanvasGroup>();
        gcg.alpha = 0.75f; gcg.blocksRaycasts = false; gcg.interactable = false;
        // Dim the source in place (CanvasGroup alpha: no layout change, restored on end).
        _rlSrcGroup = src.GetComponent<CanvasGroup>();
        if (_rlSrcGroup == null) _rlSrcGroup = src.gameObject.AddComponent<CanvasGroup>();
        _rlSrcAlpha = _rlSrcGroup.alpha;
        _rlSrcGroup.alpha = 0.35f;
        // Insertion bar: a plain accent Image, positioned per frame; never a raycast target.
        var barGo = UGuiPrimitives.NewChild("ReorderBar", parent);
        _rlBarRt = barGo.GetComponent<RectTransform>();
        _rlBarRt.anchorMin = _rlBarRt.anchorMax = _rlBarRt.pivot = new Vector2(0.5f, 0.5f);
        IgnoreLayout(barGo);
        var img = barGo.AddComponent<Image>();
        img.color = new Color(reg.Accent.r, reg.Accent.g, reg.Accent.b, 1f);
        img.raycastTarget = false;
        barGo.SetActive(false);
        _rlBarRt.SetAsLastSibling();
        _rlGhostRt.SetAsLastSibling();   // ghost above the bar
    }

    private static void IgnoreLayout(GameObject go)
    {
        var le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        le.ignoreLayout = true;
    }

    // Give the ghost the source cell's exact on-screen rect and record the grab offset. Everything is measured in the
    // ghost PARENT's local space (the nearest Canvas): the cell usually sits under a scaled window/content chain, so
    // copying src.rect.size (cell-local units) drew the ghost at the wrong size, and pinning the ghost's centre to the
    // cursor made it jump away from where it was grabbed.
    private void MatchGhostToSource(RectTransform src)
    {
        var g = _rlGhostRt;
        if (g == null) return;
        g.localScale = Vector3.one;
        g.localRotation = Quaternion.identity;
        g.anchorMin = g.anchorMax = new Vector2(0.5f, 0.5f);   // fixed anchors: size = sizeDelta, never parent-driven
        g.pivot = src.pivot;                                    // so the pivot maps to the same point of the cell
        var parent = g.parent;
        var corners = new Vector3[4];
        src.GetWorldCorners(corners);
        Vector2 bl = parent.InverseTransformPoint(corners[0]), tr = parent.InverseTransformPoint(corners[2]);
        g.sizeDelta = new Vector2(Mathf.Abs(tr.x - bl.x), Mathf.Abs(tr.y - bl.y));
        // Offset from the press point (not the threshold-crossing point) to the cell's pivot. The cell hasn't moved
        // since the press — the scroll is locked and the grid isn't reflowed mid-drag.
        Vector2 pivotLocal = parent.InverseTransformPoint(src.position);
        _rlGrab = pivotLocal - ToGhostParentLocal(_rlPress);
    }

    // Keep the press-time grab point under the cursor: ghost pivot = pointer + grab offset (parent-local units).
    private void PlaceGhost(Vector2 mp)
    {
        if (_rlGhostRt == null) return;
        var p = ToGhostParentLocal(mp) + _rlGrab;
        _rlGhostRt.localPosition = new Vector3(p.x, p.y, 0f);
    }

    // Screen px → the ghost parent's local space. Null camera, like every hit-test here (overlay canvases).
    private Vector2 ToGhostParentLocal(Vector2 screen)
    {
        if (_rlGhostParent != null
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(_rlGhostParent, screen, null, out var lp)) return lp;
        var t = _rlGhostRt != null ? _rlGhostRt.parent : null;
        return t != null ? (Vector2)t.InverseTransformPoint(new Vector3(screen.x, screen.y, 0f)) : screen;
    }

    // Insertion slot (0..n, ORIGINAL indexing) under the pointer, or -1 = outside the list (drop cancels). The item
    // nearest the pointer wins (distance 0 inside a cell, so gaps between flowed grid cells still resolve); its
    // left/right half (grid) or top/bottom half (single column) picks before/after. Works for Columns>1 and the
    // FillWidth flexible grid alike because it only reads each live cell's own rect.
    private int ReorderInsertion(WindowBuilder.ReorderListReg reg, Vector2 mp, out int cellIdx, out bool after)
    {
        cellIdx = -1; after = false;
        if (_rlZone == null || !RectTransformUtility.RectangleContainsScreenPoint(_rlZone, mp, null)) return -1;
        if (FrontWindowBlocks(mp, FindWindowRoot(reg.List))) return -1;
        var n = LiveCount(reg);
        var bestD = float.MaxValue;
        var bestLp = Vector2.zero;
        for (var i = 0; i < n; i++)
        {
            var cell = reg.Cells[i];
            if (cell == null || !cell.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(cell, mp, null, out var lp)) continue;
            var rc = cell.rect;
            var dx = Mathf.Max(0f, Mathf.Max(rc.xMin - lp.x, lp.x - rc.xMax));
            var dy = Mathf.Max(0f, Mathf.Max(rc.yMin - lp.y, lp.y - rc.yMax));
            var d = dx * dx + dy * dy;   // cells share one scale, so local distances compare across cells
            if (d < bestD) { bestD = d; cellIdx = i; bestLp = lp; }
        }
        if (cellIdx < 0) return -1;
        var c = reg.Cells[cellIdx].rect.center;
        after = reg.Grid ? bestLp.x > c.x : bestLp.y < c.y;   // local y is up
        return after ? cellIdx + 1 : cellIdx;
    }

    private void PlaceInsertionBar(WindowBuilder.ReorderListReg reg, Vector2 mp)
    {
        if (_rlBarRt == null) return;
        var ins = ReorderInsertion(reg, mp, out var idx, out var after);
        if (ins < 0) { if (_rlBarRt.gameObject.activeSelf) _rlBarRt.gameObject.SetActive(false); return; }
        var cell = reg.Cells[idx];
        var rc = cell.rect;
        Vector3 edge, a, b;
        if (reg.Grid)
        {
            var x = after ? rc.xMax : rc.xMin;
            edge = cell.TransformPoint(new Vector3(x, rc.center.y, 0f));
            a = cell.TransformPoint(new Vector3(x, rc.yMin, 0f)); b = cell.TransformPoint(new Vector3(x, rc.yMax, 0f));
        }
        else
        {
            var y = after ? rc.yMin : rc.yMax;
            edge = cell.TransformPoint(new Vector3(rc.center.x, y, 0f));
            a = cell.TransformPoint(new Vector3(rc.xMin, y, 0f)); b = cell.TransformPoint(new Vector3(rc.xMax, y, 0f));
        }
        var ps = _rlBarRt.parent != null ? _rlBarRt.parent.lossyScale.x : 1f;
        var len = (b - a).magnitude / (ps > 0f ? ps : 1f);   // world (screen px) → the bar parent's canvas units
        _rlBarRt.sizeDelta = reg.Grid ? new Vector2(ReorderBarThickness, len) : new Vector2(len, ReorderBarThickness);
        _rlBarRt.position = edge;
        if (!_rlBarRt.gameObject.activeSelf) _rlBarRt.gameObject.SetActive(true);
    }

    private void EndReorder(bool commit)
    {
        var reg = _rl;
        if (commit && reg != null && reg.List != null)
        {
            var ins = ReorderInsertion(reg, Input.mousePosition, out _, out _);
            var n = LiveCount(reg);
            if (ins >= 0 && _rlFrom >= 0 && _rlFrom < n)
            {
                var to = Mathf.Clamp(ins > _rlFrom ? ins - 1 : ins, 0, n - 1);   // remove-then-insert index
                if (to != _rlFrom)
                {
                    try { reg.OnReorder(_rlFrom, to); }
                    catch { if (_throwLogged++ == 0) Debug.LogWarning("[Window] reorder cb threw (rate-limited)"); }
                }
            }
        }
        if (_rlGhost != null) Object.Destroy(_rlGhost);
        if (_rlBarRt != null) Object.Destroy(_rlBarRt.gameObject);
        if (_rlSrcGroup != null) _rlSrcGroup.alpha = _rlSrcAlpha;
        _rlGhost = null; _rlGhostRt = null; _rlGhostParent = null; _rlBarRt = null; _rlSrcGroup = null;
        UnlockScroll();
        _rl = null; _rlFrom = -1; _rlDragging = false; _rlZone = null;
    }

    // Switch the enclosing ScrollRect's axes off for the press. ScrollRect.OnDrag skips a disabled axis, so the
    // EventSystem drag that bubbles up from the item's Button cannot scroll the grid; StopMovement kills any fling.
    // (The `vertical` setter is a plain field write — no relayout; the framework's scrollbars are AutoHide, whose
    // visibility doesn't depend on the axis flag.) The wheel is also inert for the press, which is harmless.
    private void LockScroll(ScrollRect? sr)
    {
        _rlScroll = sr;
        if (sr == null) return;
        _rlScrollH = sr.horizontal; _rlScrollV = sr.vertical;
        sr.horizontal = false; sr.vertical = false;
        sr.StopMovement();
    }

    private void UnlockScroll()
    {
        if (_rlScroll != null) { _rlScroll.horizontal = _rlScrollH; _rlScroll.vertical = _rlScrollV; }
        _rlScroll = null;
    }
}
