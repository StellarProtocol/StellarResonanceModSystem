using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

// Wheel / press stops a coasting list. uGUI's ScrollRect zeroes its fling velocity only on a new BeginDrag: a wheel step
// (OnScroll) just offsets the content, and the leftover inertia from an earlier drag keeps gliding through and after it
// (user report). So on any wheel input or mouse press over a Stellar scroll view, StopMovement() it first; the wheel step
// itself is still applied by the EventSystem's OnScroll as usual.
//
// Done here (legacy Input poll) rather than as a Harmony prefix on ScrollRect.OnScroll: no game-wide detour on a hot uGUI
// method, and ONLY the builder's ScrollRects (ScrollElement, VirtualList/VariableList/dropdown viewports) are touched —
// the game's own ScrollRects never are. Order vs the EventSystem doesn't matter: inertia is applied in ScrollRect.LateUpdate,
// after every Update, so zeroing velocity anywhere in this frame's Update kills it before the next inertia step.
public sealed partial class WindowInteractionTicker
{
    internal readonly List<ScrollRect> ScrollViews = new();   // registered by the builder (RegisterScrollView); pruned in Prune

    private void PruneScrollViews()
    {
        for (var i = ScrollViews.Count - 1; i >= 0; i--) if (ScrollViews[i] == null) ScrollViews.RemoveAt(i);
    }

    private void TickScrollInertiaStop()
    {
        var wheel = Input.mouseScrollDelta;
        var input = wheel.x != 0f || wheel.y != 0f
                    || Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2);
        if (!input) return;   // the common frame: two Input reads, no list walk
        var mp = (Vector2)Input.mousePosition;
        var popup = ModalPopupLayer?.Invoke();
        for (var i = 0; i < ScrollViews.Count; i++)
        {
            var sr = ScrollViews[i];
            if (sr == null || !sr.gameObject.activeInHierarchy) continue;
            if (sr.velocity == Vector2.zero) continue;   // not coasting — nothing to stop (cheap, before any hit-test)
            var rt = sr.viewport != null ? sr.viewport : sr.GetComponent<RectTransform>();
            if (rt == null || !RectTransformUtility.RectangleContainsScreenPoint(rt, mp, null)) continue;
            // Same owner rule as hover: a window in front of this list (FrontWindowBlocks), or an open dropdown popup
            // that isn't this list, owns the pointer — its wheel/press is not meant for the list underneath.
            if (HoverBlocked(mp, rt, popup)) continue;
            sr.StopMovement();
        }
    }
}
