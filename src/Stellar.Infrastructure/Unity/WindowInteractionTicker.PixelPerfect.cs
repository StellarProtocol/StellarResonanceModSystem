using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

// pixelPerfect while things MOVE, per canvas. pixelPerfect snaps graphics to the physical pixel grid (crisp text) but
// re-snaps every frame, so a moving window's contents step by whole pixels and the canvas re-snaps + rebatches all its
// graphics each frame — so it goes OFF while UI moves and back ON once it settles. Every flip rebuilds the canvas it is
// set on (25-45 ms for the shared root measured in game), so:
//  • a window with its OWN nested canvas (IsolateCanvas / OverrideSortingOrder: overridePixelPerfect, set at attach in
//    WindowRenderer.AttachNestedCanvas) flips ONLY that canvas for motion inside it — the root and every other window are
//    not rebuilt;
//  • motion in any other window flips the shared ROOT canvas, as before;
//  • a FOLLOWER canvas (a ConditionalElement.KeepAlive tab's own canvas, overridePixelPerfect, WindowBuilder.KeepAlive.cs)
//    flips for motion inside it (a list scrolled in that tab: only the tab re-snaps) AND whenever its enclosing canvas
//    flips (the window being dragged), so while shown it looks exactly like content of its window. A DISABLED follower
//    (a parked tab) is never flipped: re-snapping it would rebuild a tab nobody sees; it catches up when shown.
// Each canvas settles independently (ScrollSettleDelay after ITS last motion).
public sealed partial class WindowInteractionTicker
{
    private const float ScrollSettleDelay = 0.15f;   // keep pixelPerfect off this long after the last drag/glide/window motion (no flicker on a momentary pause)

    // One pixelPerfect switch: the root canvas, or one window's own nested canvas. Kind/Src are [UiPerf] flip
    // attribution only (what moved last), turned into a string only when diagnostics log a flip.
    private sealed class PpTarget
    {
        public Canvas? Canvas;
        public RectTransform? Root;   // the window root owning a nested canvas; null for the shared root canvas
        public string Id = "root";
        public float LastMove = -999f;   // Time.unscaledTime of this canvas's last USER motion
        public byte Kind;                // 0 scroll, 1 drag, 2 resize
        public Transform? Src;
        public bool Follower;            // also off while its enclosing canvas is (Outer chain)
        public PpTarget? Outer;          // follower only: the innermost canvas target enclosing it (resolved lazily)
        public bool OuterKnown;
    }

    private readonly PpTarget _ppRoot = new();
    private readonly List<PpTarget> _ppLocal = new();

    private RectTransform? _motionTarget;              // the window being dragged / resized (null = none, or not seen yet)
    private Vector2 _motionPrevPos, _motionPrevSize;   // its anchoredPosition / sizeDelta last frame
    private bool _motionEngaged;                       // this drag/resize hold has moved: counts as motion until release

    /// <summary>WindowRenderer: <paramref name="root"/>'s window has its own <paramref name="canvas"/> with
    /// overridePixelPerfect on — motion inside it flips only that canvas.</summary>
    internal void AddLocalPixelPerfect(RectTransform root, Canvas canvas, string windowId, bool follower = false)
    {
        _ppLocal.Add(new PpTarget { Canvas = canvas, Root = root, Id = windowId, Follower = follower });
        ForgetOuters();   // a window registers its canvas AFTER its tabs' (mount order): re-resolve every chain
    }

    private void PrunePixelPerfectTargets()
    {
        var removed = false;
        for (var i = _ppLocal.Count - 1; i >= 0; i--)
            if (_ppLocal[i].Root == null || _ppLocal[i].Canvas == null) { _ppLocal.RemoveAt(i); removed = true; }
        if (removed) ForgetOuters();
    }

    private void ForgetOuters()
    {
        for (var i = 0; i < _ppLocal.Count; i++) { _ppLocal[i].OuterKnown = false; _ppLocal[i].Outer = null; }
    }

    // The canvas that owns a motion source: the INNERMOST registered canvas it lives under, else the root.
    // Called only on frames with motion; a handful of entries.
    private PpTarget TargetOf(Transform src) => Innermost(src, null) ?? _ppRoot;

    // Innermost local target whose root contains t (excluding `self`); null = none.
    private PpTarget? Innermost(Transform t, PpTarget? self)
    {
        PpTarget? best = null;
        for (var i = 0; i < _ppLocal.Count; i++)
        {
            var p = _ppLocal[i];
            if (p == self || p.Root == null || !t.IsChildOf(p.Root)) continue;
            if (self?.Root != null && p.Root == self.Root) continue;
            if (best == null || p.Root.IsChildOf(best.Root!)) best = p;
        }
        return best;
    }

    // Latest motion that keeps t's canvas off: its own, plus (follower) every enclosing canvas's.
    private float LastMoveOf(PpTarget t)
    {
        var last = t.LastMove;
        for (var o = t; o.Follower;)
        {
            if (!o.OuterKnown) { o.Outer = o.Root != null ? Innermost(o.Root, o) ?? _ppRoot : _ppRoot; o.OuterKnown = true; }
            o = o.Outer!;
            if (o.LastMove > last) last = o.LastMove;
        }
        return last;
    }

    private void MarkMotion(Transform src, byte kind, float now)
    {
        var t = TargetOf(src);
        t.LastMove = now; t.Kind = kind; t.Src = src;
    }

    // Latch window drag / resize MOTION with the same held-gesture rule as a list drag-scroll (HeldMotionRule): a press on
    // a drag handle / resize grip flips nothing (holding a handle used to turn pixelPerfect off and soften all text until
    // release); the FIRST real move turns it off, and it stays off for the rest of the hold, pauses included, until
    // release — then back on after the settle. (The previous rule re-crisped 0.15 s after the window held still
    // mid-drag, so a slow drag with pauses flipped OFF/ON at every step.) The target's position and size are seeded the
    // first frame it is seen (the press frame's drag tick hasn't run yet, so no motion is counted for the press) and
    // forgotten, with the hold, when the drag ends. Allocation-free.
    private void TickWindowMotion()
    {
        RectTransform? t = _activeWinDrag;
        if (t == null && _activeResize >= 0 && _activeResize < DragResizers.Count) t = DragResizers[_activeResize].Target;
        if (t == null) { _motionTarget = null; _motionEngaged = false; return; }
        var pos = t.anchoredPosition;
        var size = t.sizeDelta;
        if (_motionTarget == null || _motionTarget != t)
        {
            _motionTarget = t; _motionPrevPos = pos; _motionPrevSize = size; _motionEngaged = false;
            return;
        }
        // 0.5 px threshold ignores sub-pixel jitter (a clamp re-applying the same spot); position covers top/left edges.
        var moved = Mathf.Abs(pos.x - _motionPrevPos.x) > 0.5f || Mathf.Abs(pos.y - _motionPrevPos.y) > 0.5f
            || Mathf.Abs(size.x - _motionPrevSize.x) > 0.5f || Mathf.Abs(size.y - _motionPrevSize.y) > 0.5f;
        // Windows never glide; the drag/resize state itself is the "held" signal (cleared by the ticker on release).
        if (HeldMotionRule.CountsAsMotion(pressed: true, moved, gliding: false, ref _motionEngaged))
            MarkMotion(t, _activeWinDrag == null ? (byte)2 : (byte)1, Time.unscaledTime);
        _motionPrevPos = pos; _motionPrevSize = size;
    }

    // Detect per-frame CONTINUOUS user scroll motion. A ScrollRect's content rect moving between frames while the user
    // drags that view or it glides after the drag (IsUserScrolling, .ScrollViews.cs) = the user is scrolling a list (e.g.
    // the chat log). Like a window drag/resize, that re-snaps + rebatches its canvas every frame while pixelPerfect is ON
    // — the ~15ms chat-scroll FPS drop. Latch the last-motion time on the owning canvas so SyncPixelPerfect keeps it OFF
    // for ScrollSettleDelay after the last move. One-shot moves are NOT scrolling, because one move is one ordinary
    // rebuild while flipping pixelPerfect for it costs two canvas rebuilds (off + on, 25-50 ms each on the root):
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
            // Cached GameObject: one destroyed-check + one activeInHierarchy per view per frame (was c==null, Sr==null,
            // Component.gameObject, activeInHierarchy). The ScrollRect sits above its content, and nothing destroys a
            // ScrollRect component alone, so a live content GameObject implies a live Sr.
            if (v.ContentGo == null || !v.ContentGo.activeInHierarchy) { v.PrevY = float.NaN; continue; }
            var y = c.anchoredPosition.y;
            // 0.5px threshold ignores sub-pixel jitter; NaN prev (first/re-activated frame) = no move this frame. An engaged
            // hold still counts on still frames, so one slow drag-scroll is exactly one OFF and one ON (after release).
            var moved = !float.IsNaN(v.PrevY) && Mathf.Abs(y - v.PrevY) > 0.5f;
            if (moved) _contentMovedThisFrame = true;   // ANY move (user or programmatic) shifts rows under the pointer: hover rescan (.Hover.cs)
            if (IsUserScrolling(v, moved)) MarkMotion(c, 0, now);
            v.PrevY = y;
        }
    }

    // Per canvas: OFF while its own content moves (drag / resize / drag-scroll / glide), ON otherwise so static UI stays
    // crisp. Drag / resize / drag-scroll count from the gesture's first real move until release (HeldMotionRule): a press
    // without movement never flips, and pauses mid-gesture don't re-crisp. Guarded so a canvas rebuild only happens when that canvas's state actually flips.
    private void SyncPixelPerfect()
    {
        if (_canvasComp == null) _canvasComp = GetComponent<Canvas>();
        if (_canvasComp == null) return;
        _ppRoot.Canvas = _canvasComp;
        var now = Time.unscaledTime;
        Sync(_ppRoot, now);
        for (var i = 0; i < _ppLocal.Count; i++) Sync(_ppLocal[i], now);
    }

    private void Sync(PpTarget t, float now)
    {
        var c = t.Canvas;
        if (c == null) return;
        if (t.Follower && !c.enabled) return;   // a parked KeepAlive tab: never re-snapped while nobody sees it
        var want = now - (t.Follower ? LastMoveOf(t) : t.LastMove) >= ScrollSettleDelay;
        if (c.pixelPerfect == want) return;
        // Every flip re-snaps + rebatches that canvas — [UiPerf] counts them with the canvas, a reason and their cost.
        var t0 = UiPerfStats.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        c.pixelPerfect = want;
        if (t0 != 0L) UiPerfStats.PixelPerfectFlip(want, t.Id, want ? "rest" : FlipReason(t), t0);
    }

    private static string FlipReason(PpTarget t)
        => (t.Kind == 0 ? "scroll:" : t.Kind == 1 ? "drag:" : "resize:") + WindowIdOf(t.Src);
}
