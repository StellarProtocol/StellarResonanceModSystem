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
//  • motion in any other window flips the shared ROOT canvas, as before.
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
    }

    private readonly PpTarget _ppRoot = new();
    private readonly List<PpTarget> _ppLocal = new();

    private RectTransform? _motionTarget;              // the window being dragged / resized (null = none, or not seen yet)
    private Vector2 _motionPrevPos, _motionPrevSize;   // its anchoredPosition / sizeDelta last frame

    /// <summary>WindowRenderer: <paramref name="root"/>'s window has its own <paramref name="canvas"/> with
    /// overridePixelPerfect on — motion inside it flips only that canvas.</summary>
    internal void AddLocalPixelPerfect(RectTransform root, Canvas canvas, string windowId)
        => _ppLocal.Add(new PpTarget { Canvas = canvas, Root = root, Id = windowId });

    private void PrunePixelPerfectTargets()
    {
        for (var i = _ppLocal.Count - 1; i >= 0; i--)
            if (_ppLocal[i].Root == null || _ppLocal[i].Canvas == null) _ppLocal.RemoveAt(i);
    }

    // The canvas that owns a motion source: the innermost registered window canvas it lives under, else the root.
    // Called only on frames with motion; a handful of entries.
    private PpTarget TargetOf(Transform src)
    {
        for (var i = 0; i < _ppLocal.Count; i++)
        {
            var root = _ppLocal[i].Root;
            if (root != null && src.IsChildOf(root)) return _ppLocal[i];
        }
        return _ppRoot;
    }

    private void MarkMotion(Transform src, byte kind, float now)
    {
        var t = TargetOf(src);
        t.LastMove = now; t.Kind = kind; t.Src = src;
    }

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
            if (c == null || v.Sr == null || !c.gameObject.activeInHierarchy) { v.PrevY = float.NaN; continue; }
            var y = c.anchoredPosition.y;
            // 0.5px threshold ignores sub-pixel jitter; NaN prev (first/re-activated frame) = no motion counted.
            if (!float.IsNaN(v.PrevY) && Mathf.Abs(y - v.PrevY) > 0.5f && IsUserScrolling(v)) MarkMotion(c, 0, now);
            v.PrevY = y;
        }
    }

    // Per canvas: OFF while its own content moves (drag / resize / drag-scroll / glide), ON otherwise so static UI stays
    // crisp. Drag / resize count only while the window actually MOVES (TickWindowMotion): holding still on a handle
    // re-crisps after the settle. Guarded so a canvas rebuild only happens when that canvas's state actually flips.
    private void SyncPixelPerfect()
    {
        if (_canvasComp == null) _canvasComp = GetComponent<Canvas>();
        if (_canvasComp == null) return;
        _ppRoot.Canvas = _canvasComp;
        var now = Time.unscaledTime;
        Sync(_ppRoot, now);
        for (var i = 0; i < _ppLocal.Count; i++) Sync(_ppLocal[i], now);
    }

    private static void Sync(PpTarget t, float now)
    {
        var c = t.Canvas;
        if (c == null) return;
        var want = now - t.LastMove >= ScrollSettleDelay;
        if (c.pixelPerfect == want) return;
        // Every flip re-snaps + rebatches that canvas — [UiPerf] counts them with the canvas, a reason and their cost.
        var t0 = UiPerfStats.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        c.pixelPerfect = want;
        if (t0 != 0L) UiPerfStats.PixelPerfectFlip(want, t.Id, want ? "rest" : FlipReason(t), t0);
    }

    private static string FlipReason(PpTarget t)
        => (t.Kind == 0 ? "scroll:" : t.Kind == 1 ? "drag:" : "resize:") + WindowIdOf(t.Src);
}
