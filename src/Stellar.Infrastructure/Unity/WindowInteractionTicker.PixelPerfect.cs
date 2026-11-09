using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

public sealed partial class WindowInteractionTicker
{
    private float _lastScrollMoveTime = -999f;                     // Time.unscaledTime of the last USER scroll motion
    private const float ScrollSettleDelay = 0.15f;                 // keep pixelPerfect off this long after the last drag/glide/window motion (no flicker on a momentary pause)
    private RectTransform? _motionTarget;                          // the window being dragged / resized (null = none, or not seen yet)
    private Vector2 _motionPrevPos, _motionPrevSize;               // its anchoredPosition / sizeDelta last frame
    private float _lastWinMoveTime = -999f;                        // Time.unscaledTime of the last detected window move / resize
    // [UiPerf] flip attribution only (read when pixelPerfect flips with diagnostics on): what moved last.
    private ScrollView? _lastScrollView;
    private bool _lastWinMoveWasResize;

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
        {
            _lastWinMoveTime = Time.unscaledTime;
            _lastWinMoveWasResize = _activeWinDrag == null;
        }
        _motionPrevPos = pos; _motionPrevSize = size;
    }

    // Detect per-frame CONTINUOUS user scroll motion. A ScrollRect's content rect moving between frames while the user
    // drags that view or it glides after the drag (IsUserScrolling, .ScrollViews.cs) = the user is scrolling a list (e.g.
    // the chat log). Like a window drag/resize, that re-snaps + rebatches the whole (isolated) canvas every frame while
    // pixelPerfect is ON — the ~15ms chat-scroll FPS drop. Latch the last-motion time so SyncPixelPerfect keeps
    // pixelPerfect OFF for ScrollSettleDelay after the last move. One-shot moves are NOT scrolling, because one move is
    // one ordinary rebuild while flipping pixelPerfect for it costs two whole-canvas rebuilds (off + on, 25-50 ms each):
    // PROGRAMMATIC moves (a chat re-pin to the bottom on a new message, a list growing/shrinking, a rebind) and WHEEL
    // ticks (each tick moves the content once — measured 2 flips ≈ 60 ms per tick when the wheel counted).
    // Called from Update() immediately before SyncPixelPerfect().
    private void TickScrollActivity()
    {
        var now = Time.unscaledTime;
        for (var i = 0; i < ScrollViews.Count; i++)
        {
            var v = ScrollViews[i];
            var c = v.Content;
            // A re-activated view must not register a spurious jump on its first frame back → seed NaN while inactive.
            if (c == null || v.Sr == null || !c.gameObject.activeInHierarchy) { v.PrevY = float.NaN; continue; }
            var y = c.anchoredPosition.y;
            // 0.5px threshold ignores sub-pixel jitter; NaN prev (first/re-activated frame) = no motion counted.
            if (!float.IsNaN(v.PrevY) && Mathf.Abs(y - v.PrevY) > 0.5f && IsUserScrolling(v))
            {
                _lastScrollMoveTime = now;
                _lastScrollView = v;
            }
            v.PrevY = y;
        }
    }

    // pixelPerfect snaps graphics to the physical pixel grid (crisp text), but it re-snaps every frame — so a
    // MOVING window's contents step by whole pixels instead of gliding. Turn it off while a window is being
    // dragged or resized, OR while a list is being drag-scrolled / gliding (TickScrollActivity), back on otherwise so static UI
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
        if (_canvasComp.pixelPerfect == want) return;
        // Every flip re-snaps + rebatches the whole canvas — [UiPerf] counts them with a reason and their cost.
        var t0 = UiPerfStats.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        _canvasComp.pixelPerfect = want;
        if (t0 != 0L) UiPerfStats.PixelPerfectFlip(want, want ? "rest" : FlipReason(winMoving), t0);
    }

    private string FlipReason(bool winMoving)
    {
        if (winMoving)
            return (_lastWinMoveWasResize ? "resize:" : "drag:") + WindowIdOf(_motionTarget);
        return "scroll:" + WindowIdOf(_lastScrollView?.Content);
    }
}
