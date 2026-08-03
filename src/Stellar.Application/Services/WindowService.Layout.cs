using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Window position persistence (SP1 Settings migration) — mirrors HudService.Layout. On mount a window is
/// placed at its saved rect (falling back to the spec's DefaultRect); after a titlebar drag settles, the
/// new position is saved via <see cref="LayoutStorage"/> (per active slot + resolution). The drag itself is
/// the renderer's interaction ticker moving the RectTransform; this layer detects the settled rect and
/// persists it — no per-frame disk thrash.
/// </summary>
internal sealed partial class WindowService
{
    private LayoutStorage? _storage;
    private Func<Resolution>? _resolution;

    public void AttachLayout(LayoutStorage storage, Func<Resolution> resolution)
    {
        _storage = storage;
        _resolution = resolution;
    }

    /// <summary>Layout-editor "Reset" for a mod window: drop its saved override and re-place it at the
    /// (on-screen-clamped) DefaultRect. No-ops if the id isn't a registered/mounted window, so the editor can
    /// fan a reset across all element services without knowing which owns the id.</summary>
    public void ResetRect(string id)
    {
        if (!_windows.TryGetValue(id, out var e)) return;
        e.SetVisible(true);                          // reset restores default visibility (shown)
        if (_storage != null) _storage.Remove(_storage.ActiveSlot, id);
        if (e.Token != null) ApplySavedRect(e);      // re-place if mounted; a hidden one mounts+places next tick
    }

    /// <summary>Editor-driven visibility toggle for a mod window: flip Visible (TickEntry mounts/destroys to
    /// match) and persist per slot. Uses the live rect when shown, else the last-saved/default rect.</summary>
    public void SetVisiblePersist(string id, bool visible)
    {
        if (!_windows.TryGetValue(id, out var e)) return;
        e.SetVisible(visible);
        if (_storage != null && _resolution != null)
        {
            var rect = e.Token != null ? _renderer.GetRect(e.Token)
                     : e.LastSavedRect.Width > 0 ? e.LastSavedRect : ResolveAnchoredDefault(e.Reg.Spec);
            _storage.Save(_storage.ActiveSlot, id, _resolution(), rect, visible);
        }
    }

    // Reload the saved layout for the CURRENT resolution for every mounted window (called on a resolution change).
    // ApplySavedRect reads _resolution() + _storage.Get, so it picks up the new resolution's bucket (or the
    // anchor-resolved DefaultRect fallback) and SetRect clamps it on-screen.
    internal void ReapplyLayout()
    {
        foreach (var kv in _windows)
        {
            var e = kv.Value;
            if (e.Removed || e.Token == null) continue;
            ApplySavedRect(e, applyVisibility: false);   // res change repositions but never toggles show/hide
        }
    }

    // Resolve a spec's DefaultRect against its Anchor into a top-left WindowRect in CANVAS UNITS. TopLeft returns
    // DefaultRect unchanged (legacy). Other anchors place the window's matching point at the canvas's anchor point,
    // with DefaultRect.X/Y as an offset. Canvas dims = Screen ÷ scaleFactor. Guards to DefaultRect if dims unknown.
    private WindowRect ResolveAnchoredDefault(WindowSpec spec)
    {
        var d = spec.DefaultRect;
        // Make the OFFSET/absolute position UI-scale-independent: dividing by the slider u cancels the slider term
        // folded into the canvas scaleFactor, so the rendered position = design pos × resolutionScale (NOT × u) and
        // the slider grows the window IN PLACE. Size (d.Width/d.Height) is NEVER divided — it stays canvas units so
        // it scales with the slider. The anchor BASE ((cw-w)/2, cw-w, …) is already UI-scale-independent. No-op at u=1.
        var u = (_renderer as Stellar.Application.Abstractions.IWindowCanvasMetrics)?.UiScale ?? 1f;
        if (u <= 0f) u = 1f;
        var ox = d.X / u;
        var oy = d.Y / u;
        if (spec.Anchor == WindowAnchor.TopLeft) return new WindowRect(ox, oy, d.Width, d.Height);
        var sf = CanvasScale;
        var res = _resolution?.Invoke() ?? default;
        if (sf <= 0f || res.Width <= 0 || res.Height <= 0) return new WindowRect(ox, oy, d.Width, d.Height);
        float cw = res.Width / sf, ch = res.Height / sf;   // canvas-unit screen dims
        float x = spec.Anchor switch
        {
            WindowAnchor.Left or WindowAnchor.TopLeft or WindowAnchor.BottomLeft => ox,
            WindowAnchor.Center or WindowAnchor.Top or WindowAnchor.Bottom => (cw - d.Width) / 2f + ox,
            _ => cw - d.Width + ox,   // Right / TopRight / BottomRight
        };
        float y = spec.Anchor switch
        {
            WindowAnchor.Top or WindowAnchor.TopLeft or WindowAnchor.TopRight => oy,
            WindowAnchor.Center or WindowAnchor.Left or WindowAnchor.Right => (ch - d.Height) / 2f + oy,
            _ => ch - d.Height + oy,  // Bottom / BottomLeft / BottomRight
        };
        return new WindowRect(x, y, d.Width, d.Height);
    }

    // Restore the saved rect (or DefaultRect) right after a successful mount. applyVisibility=true (mount/reset)
    // also honours a persisted hide; false (resolution change) repositions only and leaves current visibility.
    private void ApplySavedRect(Entry e, bool applyVisibility = true)
    {
        if (e.Token is null) return;
        var fallback = ResolveAnchoredDefault(e.Reg.Spec);
        if (_storage is null || _resolution is null)
        {
            _renderer.SetRect(e.Token, fallback);
            e.LastRect = e.LastSavedRect = fallback;
            return;
        }
        var (rect, visible) = _storage.Get(_storage.ActiveSlot, e.Reg.Spec.Id, _resolution(), fallback, CanvasScale);
        _renderer.SetRect(e.Token, rect);
        e.LastRect = e.LastSavedRect = rect;
        if (applyVisibility && !visible) e.SetVisible(false);   // honour a persisted hide (TickEntry destroys next tick)
    }

    // Persist once after a drag settles: the rect is unchanged since last tick (drag stopped) AND differs
    // from what's saved. Avoids a disk write every frame during the drag.
    private void PersistIfSettled(Entry e)
    {
        if (_storage is null || _resolution is null || e.Token is null
            || !(e.Reg.Spec.Draggable || e.Reg.Spec.Resizable)) return;
        var cur = _renderer.GetRect(e.Token);
        if (RectClose(cur, e.LastRect) && !RectClose(cur, e.LastSavedRect))
        {
            _storage.Save(_storage.ActiveSlot, e.Reg.Spec.Id, _resolution(), cur, e.Visible);
            e.LastSavedRect = cur;
        }
        e.LastRect = cur;
    }

    // Position AND size (resizable windows persist their grip-dragged dimensions too).
    private static bool RectClose(WindowRect a, WindowRect b)
        => Math.Abs(a.X - b.X) < 0.5f && Math.Abs(a.Y - b.Y) < 0.5f
        && Math.Abs(a.Width - b.Width) < 0.5f && Math.Abs(a.Height - b.Height) < 0.5f;
}
