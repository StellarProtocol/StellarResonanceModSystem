using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

// Window-drag start gate (WindowDragRule): a press on a window's drag handle that lands on an interactive control or
// inside a scrolling list belongs to that control, not to a window drag. Title-bar-less Draggable windows (Dungeon Hub,
// MultiTool) use the whole frame as the handle, so before this a drag inside a list moved the window instead of scrolling
// it, and press-and-drag on a button moved the window. Evaluated once, on the press edge.
public sealed partial class WindowInteractionTicker
{
    // Reused per press (RaycastAll fills it); one allocation for the ticker's lifetime.
    private readonly Il2CppSystem.Collections.Generic.List<RaycastResult> _dragRaycastScratch = new();

    // True when the press at mp may start dragging `target`. Layout edit mode short-circuits before any raycast.
    private bool WindowDragAllowed(Vector3 mp, RectTransform target)
    {
        if (LayoutEditGate.IsEditing) return true;
        var (interactive, scrolls) = PressedControl(mp, target);
        return WindowDragRule.ShouldStart(layoutEditing: false, interactive, scrolls);
    }

    // Classifies the TOPMOST uGUI hit under the pointer, walking its parents up to (not past) the dragged window's
    // root. Only the topmost hit counts: if it belongs to another window (or nothing), FrontWindowBlocks already decided
    // the press, so report "nothing interactive" and leave today's behaviour alone. mp is Input.mousePosition, which is
    // already bottom-left/y-up like PointerEventData.position (the y-flip in LayoutEditorOverlay is only for the
    // framework's y-down IInputGateway.PointerPosition).
    private (bool Interactive, bool Scrolls) PressedControl(Vector3 mp, RectTransform target)
    {
        try
        {
            var es = EventSystem.current;
            if (es == null || target == null) return (false, false);
            var ped = new PointerEventData(es) { position = new Vector2(mp.x, mp.y) };
            _dragRaycastScratch.Clear();
            es.RaycastAll(ped, _dragRaycastScratch);
            if (_dragRaycastScratch.Count == 0) return (false, false);
            var hit = _dragRaycastScratch[0].gameObject;   // RaycastAll is sorted front-to-back
            _dragRaycastScratch.Clear();
            if (hit == null) return (false, false);

            // Pointer compare: interop wrappers for the same native transform aren't guaranteed to be one managed object.
            var root = target.Pointer;
            bool interactive = false, scrolls = false;
            for (var t = hit.transform; t != null; t = t.parent)
            {
                if (t.Pointer == root) return (interactive, scrolls);
                // GetComponent<Selectable> resolves Button/Toggle/Slider/Scrollbar/Dropdown/InputField/TMP_InputField
                // natively by base type; no C# `is` on interop wrappers. IsInteractable folds in CanvasGroup.interactable,
                // so a disabled button, or a non-interactable drag sink (UGuiPrimitives.AddDragSink), stays a drag surface.
                var sel = t.GetComponent<Selectable>();
                if (sel != null && sel.isActiveAndEnabled && sel.IsInteractable()) interactive = true;
                var sr = t.GetComponent<ScrollRect>();
                if (sr != null && sr.isActiveAndEnabled && ScrollRectOverflows(sr)) scrolls = true;
            }
            return (false, false);   // walked off the top without meeting target → the hit isn't in this window
        }
        catch
        {
            if (_throwLogged++ == 0) Debug.LogWarning("[Window] drag-start control test threw (rate-limited)");
            return (false, false);   // fail open to today's behaviour: the press drags the window
        }
    }

    private static bool ScrollRectOverflows(ScrollRect sr)
    {
        var content = sr.content;
        if (content == null) return false;
        // viewport may be unset (ScrollRect then clips against its own rect); GetComponent, not an `as` cast on interop.
        var viewport = sr.viewport != null ? sr.viewport : sr.GetComponent<RectTransform>();
        if (viewport == null) return false;
        var c = content.rect; var v = viewport.rect;
        return WindowDragRule.AxisOverflows(sr.vertical, c.height, v.height)
            || WindowDragRule.AxisOverflows(sr.horizontal, c.width, v.width);
    }
}
