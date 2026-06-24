using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

// Click-away popup support (WindowSpec.DismissOnOutsideClick) + the right-click cell hook that opens those popups.
// Lives on the per-render-frame ticker so a one-frame click/key edge is never missed (a plugin can't poll this
// reliably from its throttled OnUpdate). Split out of WindowInteractionTicker to keep that file under the LoC gate.
public sealed partial class WindowInteractionTicker
{
    // Click-away dismissable popups (WindowSpec.DismissOnOutsideClick): Escape, or a mouse press outside the
    // window's Root rect, invokes Dismiss. Polled per render frame so a one-frame click edge is never missed.
    internal readonly List<(RectTransform Root, Action Dismiss)> Dismissables = new();

    // Click-away / Escape dismiss for popups (per render frame, so no edge is missed). suppressMouse = a
    // right-click opened/moved a popup THIS frame (don't let that same click also dismiss it); Escape still works.
    private void TickDismissables(bool suppressMouse)
    {
        if (Dismissables.Count == 0) return;
        var esc = Input.GetKeyDown(KeyCode.Escape);
        var mouse = !suppressMouse && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1));
        if (!esc && !mouse) return;
        var mp = Input.mousePosition;
        for (var i = Dismissables.Count - 1; i >= 0; i--)
        {
            var (root, dismiss) = Dismissables[i];
            if (root == null) { Dismissables.RemoveAt(i); continue; }
            if (!root.gameObject.activeInHierarchy) continue;
            var outside = mouse && !RectTransformUtility.RectangleContainsScreenPoint(root, mp, null);
            if (esc || outside) { try { dismiss(); } catch { if (_throwLogged++ == 0) Debug.LogWarning("[Window] dismiss cb threw (rate-limited)"); } }
        }
    }

    // Right-button-down over a registered cell fires its context-menu callback (topmost-registered wins).
    // Returns true if a cell was hit (so the caller can suppress dismissing the popup that same press opened).
    private bool TickRightClick()
    {
        var mp = Input.mousePosition;
        if (PointerOverPopup(mp)) return false;   // a right-click on the popup isn't a row right-click beneath it
        for (var i = 0; i < RightClicks.Count; i++)
        {
            var (cell, cb) = RightClicks[i];
            if (cell == null || !cell.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(cell, mp, null)) continue;
            // Don't fire if a higher-Z window covers the pointer — that window owns this click.
            if (FrontWindowBlocks(mp, FindWindowRoot(cell))) continue;
            try { cb(); } catch { if (_throwLogged++ == 0) Debug.LogWarning("[Window] right-click cb threw (rate-limited)"); }
            return true;
        }
        return false;
    }


    // True if the pointer is over an open click-away popup (context menu). The ticker's interaction hit-tests are
    // geometric (RectangleContainsScreenPoint) and ignore z-order, so without this a press on a popup that
    // overlaps a meter row / drag-slot beneath would ALSO grab that lower element. uGUI's own EventSystem already
    // routes the press to the topmost graphic (the popup's button), so we just suppress the parallel geometric path.
    private bool PointerOverPopup(Vector2 mp)
    {
        for (var i = 0; i < Dismissables.Count; i++)
        {
            var root = Dismissables[i].Root;
            if (root != null && root.gameObject.activeInHierarchy
                && RectTransformUtility.RectangleContainsScreenPoint(root, mp, null))
                return true;
        }
        return false;
    }
}
