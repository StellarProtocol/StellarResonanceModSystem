using System.Collections.Generic;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

public sealed partial class WindowInteractionTicker
{
    internal readonly List<RectTransform> ScrollContents = new();  // scroll-view content rects registered by the builder
    private readonly List<float> _scrollPrevY = new();             // parallel: previous frame's content.anchoredPosition.y per entry (NaN = unknown/inactive)
    private float _lastScrollMoveTime = -999f;                     // Time.unscaledTime of the last detected scroll motion
    private const float ScrollSettleDelay = 0.15f;                 // keep pixelPerfect off this long after the last scroll motion (so it doesn't flicker on/off between discrete wheel steps)
    private RectTransform? _motionTarget;                          // the window being dragged / resized (null = none, or not seen yet)
    private Vector2 _motionPrevPos, _motionPrevSize;               // its anchoredPosition / sizeDelta last frame
    private float _lastWinMoveTime = -999f;                        // Time.unscaledTime of the last detected window move / resize

    // Latch window drag / resize MOTION (like TickScrollActivity), so pixelPerfect drops only while the window really
    // moves: just holding the mouse on a drag handle used to turn it off and soften all text until release. The
    // target's position and size are seeded the first frame it is seen (the press frame's drag tick hasn't run yet,
    // so no motion is counted for the press) and forgotten when the drag ends. Allocation-free.
    private void TickWindowMotion()
    {
        RectTransform? t = _activeWinDrag;
        if (t == null && _activeResize >= 0 && _activeResize < DragResizers.Count) t = DragResizers[_activeResize].Target;
        if (t == null) { _motionTarget = null; return; }
        var pos = t.anchoredPosition;
        var size = t.sizeDelta;
        if (_motionTarget == null || _motionTarget != t)
        {
            _motionTarget = t; _motionPrevPos = pos; _motionPrevSize = size;
            return;
        }
        // 0.5 px threshold ignores sub-pixel jitter (a clamp re-applying the same spot); position covers top/left edges.
        if (Mathf.Abs(pos.x - _motionPrevPos.x) > 0.5f || Mathf.Abs(pos.y - _motionPrevPos.y) > 0.5f
            || Mathf.Abs(size.x - _motionPrevSize.x) > 0.5f || Mathf.Abs(size.y - _motionPrevSize.y) > 0.5f)
            _lastWinMoveTime = Time.unscaledTime;
        _motionPrevPos = pos; _motionPrevSize = size;
    }

    // Detect per-frame scroll motion. A ScrollRect's content rect moving between frames = the user is scrolling a
    // list (e.g. the chat log). Like a window drag/resize, that re-snaps + rebatches the whole (isolated) canvas
    // every frame while pixelPerfect is ON — the ~15ms chat-scroll FPS drop. Latch the last-motion time so
    // SyncPixelPerfect keeps pixelPerfect OFF for ScrollSettleDelay after the last move (no flicker between
    // discrete wheel steps). Called from Update() immediately before SyncPixelPerfect().
    private void TickScrollActivity()
    {
        for (var i = 0; i < ScrollContents.Count; i++)
        {
            while (_scrollPrevY.Count <= i) _scrollPrevY.Add(float.NaN);   // mirror the _hoverState grow-loop
            var c = ScrollContents[i];
            // A re-activated view must not register a spurious jump on its first frame back → seed NaN while inactive.
            if (c == null || !c.gameObject.activeInHierarchy) { _scrollPrevY[i] = float.NaN; continue; }
            var y = c.anchoredPosition.y;
            // 0.5px threshold ignores sub-pixel jitter; NaN prev (first/re-activated frame) = no motion counted.
            if (!float.IsNaN(_scrollPrevY[i]) && Mathf.Abs(y - _scrollPrevY[i]) > 0.5f) _lastScrollMoveTime = Time.unscaledTime;
            _scrollPrevY[i] = y;
        }
    }

    // pixelPerfect snaps graphics to the physical pixel grid (crisp text), but it re-snaps every frame — so a
    // MOVING window's contents step by whole pixels instead of gliding. Turn it off while a window is being
    // dragged or resized, OR while a list is being scrolled (TickScrollActivity), back on otherwise so static UI
    // stays crisp. Guarded so the canvas rebuild only happens when the state actually flips.
    private void SyncPixelPerfect()
    {
        if (_canvasComp == null) _canvasComp = GetComponent<Canvas>();
        if (_canvasComp == null) return;
        // pixelPerfect ON keeps glyphs/graphics on whole pixels (crisp text) but re-snaps every frame, which makes
        // a MOVING window's contents step by whole pixels — so turn it off at ANY scale while a window is being
        // dragged or resized, or while a scroll view's content is still moving (scroll of the chat log re-snapped
        // + rebatched ~160 graphics/frame with it ON), then back on so static UI re-crisps once movement settles.
        // Drag / resize count only while the window actually MOVES (TickWindowMotion), with the same settle as scrolling:
        // holding still on a handle re-crisps after 0.15 s, release re-crisps after the settle as before.
        var scrolling = Time.unscaledTime - _lastScrollMoveTime < ScrollSettleDelay;
        var winMoving = Time.unscaledTime - _lastWinMoveTime < ScrollSettleDelay;
        var moving = winMoving || scrolling;
        var want = !moving;
        if (_canvasComp.pixelPerfect != want) _canvasComp.pixelPerfect = want;
    }
}
