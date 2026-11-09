using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

// The builder's scroll views (ScrollElement, VirtualList/VariableList/dropdown viewports — registered via
// WindowBuilder.RegisterScrollView) and the USER input on them. Two consumers:
//  • Wheel / press stops a coasting list. uGUI's ScrollRect zeroes its fling velocity only on a new BeginDrag: a wheel
//    step (OnScroll) just offsets the content, and the leftover inertia from an earlier drag kept gliding through and
//    after it (user report). Done from this legacy-Input poll rather than a Harmony prefix on ScrollRect.OnScroll: no
//    game-wide detour on a hot uGUI method, and the game's own ScrollRects are never touched. Order vs the EventSystem
//    doesn't matter — inertia is applied in ScrollRect.LateUpdate, after every Update.
//  • TickScrollActivity (.PixelPerfect.cs) counts content motion as "scrolling" ONLY while the user is driving that view
//    (IsUserScrolling). Counting every content move flipped canvas.pixelPerfect on PROGRAMMATIC moves (chat re-pinning to
//    the bottom on a new message, a list growing/shrinking, a rebind) — each flip re-snaps + rebatches the whole canvas,
//    2 × 30-50 ms canvas rebuilds while idle in town (fw 69a41c4 log: 88 [Hitch] lines in ~10 min).
public sealed partial class WindowInteractionTicker
{
    internal sealed class ScrollView
    {
        public ScrollRect Sr = null!;
        public RectTransform Content = null!;   // moves while scrolling (motion source for the pixelPerfect latch)
        public RectTransform Hit = null!;       // the ScrollRect's own rect: viewport AND its scrollbar
        public float PrevY = float.NaN;         // previous frame's Content.anchoredPosition.y (NaN = unknown/inactive)
        public float WheelTime = -999f;         // Time.unscaledTime of the last wheel input over this view
        public bool Pressed;                    // left button went down over this view and is still held (drag / scrollbar)
    }

    internal readonly List<ScrollView> ScrollViews = new();
    private bool _anyScrollPressed;
    // Wheel steps are discrete: OnScroll may land this frame or next, then the content settles — keep the view
    // "user-driven" this long after a wheel tick so the motion it causes is attributed to the user.
    private const float UserWheelWindow = 0.3f;

    internal void AddScrollView(RectTransform content, ScrollRect sr)
        => ScrollViews.Add(new ScrollView { Sr = sr, Content = content, Hit = sr.GetComponent<RectTransform>() });

    private void PruneScrollViews()
    {
        for (var i = ScrollViews.Count - 1; i >= 0; i--)
        {
            var v = ScrollViews[i];
            if (v.Sr == null || v.Content == null || v.Hit == null) ScrollViews.RemoveAt(i);
        }
    }

    /// <summary>True while the user drives this view: a wheel tick over it just now, the left button held since a press on
    /// it (content drag or scrollbar drag), or fling inertia (ScrollRect builds velocity only from a user drag and zeroes
    /// it below 1 px/s; programmatic content moves never set it).</summary>
    private static bool IsUserScrolling(ScrollView v, float now)
        => now - v.WheelTime < UserWheelWindow || v.Pressed || v.Sr.velocity.sqrMagnitude > 1f;

    // Per frame, before TickScrollActivity: latch wheel / press on the views under the pointer and stop their inertia.
    private void TickScrollInput()
    {
        if (_anyScrollPressed && !Input.GetMouseButton(0))
        {
            for (var i = 0; i < ScrollViews.Count; i++) ScrollViews[i].Pressed = false;
            _anyScrollPressed = false;
        }
        var wheel = Input.mouseScrollDelta;
        var wheeled = wheel.x != 0f || wheel.y != 0f;
        var leftDown = Input.GetMouseButtonDown(0);
        if (!wheeled && !leftDown && !Input.GetMouseButtonDown(1) && !Input.GetMouseButtonDown(2)) return;   // the common frame
        var mp = (Vector2)Input.mousePosition;
        var popup = ModalPopupLayer?.Invoke();
        var now = Time.unscaledTime;
        for (var i = 0; i < ScrollViews.Count; i++)
        {
            var v = ScrollViews[i];
            if (v.Sr == null || v.Hit == null || !v.Sr.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(v.Hit, mp, null)) continue;
            // Same owner rule as hover: a window in front of this list (FrontWindowBlocks), or an open dropdown popup
            // that isn't this list, owns the pointer — its wheel/press is not meant for the list underneath.
            if (HoverBlocked(mp, v.Hit, popup)) continue;
            if (wheeled) v.WheelTime = now;
            if (leftDown) { v.Pressed = true; _anyScrollPressed = true; }
            if (v.Sr.velocity != Vector2.zero) v.Sr.StopMovement();   // kill the coast; the wheel step still applies
        }
    }

    // [UiPerf] flip reason: the Stellar window (root GameObject name == WindowSpec.Id) that owns a transform. Walks by
    // Transform (no RectTransform cast — `is RectTransform` is unreliable on IL2CPP). Diagnostics only, called per flip.
    private static string WindowIdOf(Transform? t)
    {
        if (t == null) return "?";
        while (t.parent != null && t.parent.parent != null) t = t.parent;
        return t.name;
    }
}
