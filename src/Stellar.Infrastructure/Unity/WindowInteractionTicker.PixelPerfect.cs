using System.Collections.Generic;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

public sealed partial class WindowInteractionTicker
{
    internal readonly List<RectTransform> ScrollContents = new();  // scroll-view content rects registered by the builder
    private readonly List<float> _scrollPrevY = new();             // parallel: previous frame's content.anchoredPosition.y per entry (NaN = unknown/inactive)
    private float _lastScrollMoveTime = -999f;                     // Time.unscaledTime of the last detected scroll motion
    private const float ScrollSettleDelay = 0.15f;                 // keep pixelPerfect off this long after the last scroll motion (so it doesn't flicker on/off between discrete wheel steps)

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
        var scrolling = Time.unscaledTime - _lastScrollMoveTime < ScrollSettleDelay;
        var moving = _activeWinDrag != null || _activeResize >= 0 || scrolling;
        var want = !moving;
        if (_canvasComp.pixelPerfect != want) _canvasComp.pixelPerfect = want;
    }
}
