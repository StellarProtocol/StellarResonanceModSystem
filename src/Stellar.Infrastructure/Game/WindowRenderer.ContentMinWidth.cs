using Stellar.Abstractions.Domain;
using UnityEngine;
using WindowToken = Stellar.Infrastructure.Game.WindowBuilder.WindowToken;

namespace Stellar.Infrastructure.Game;

// Content-driven minimum width (RowElement.ConstrainsWindowMinWidth). The token measures (WindowToken.MeasureMinWidth);
// this side ENFORCES the result through the paths that already exist rather than adding new ones:
//   • the resize grip — the ticker's DragResizers entry for this window carries Min; we rewrite its Min.x, so
//     TickResize's existing clamp holds the new floor with no ticker change;
//   • every SetRect (mount/restore from LayoutStorage, slot switch, res change, plugin SetRect) — SetRect already
//     clamps size to that same DragResizers Min, so a saved layout narrower than the content is widened on load;
//   • a runtime GROWTH (language / font scale / theme) — widen once, here, through SetRect.
// Never shrinks the window: a smaller requirement only lowers the grip floor. Resizable windows only (a content-fit
// window already sizes to its content, and only Resizable windows have a DragResizers entry).
internal sealed partial class WindowRenderer
{
    // Run after the window's Apply (ApplyValues) and once at mount (force: the first layout was forced immediate,
    // so the cached preferred widths are current) — the mount call lands BEFORE WindowService.ApplySavedRect, so the
    // restore's SetRect clamps against the content minimum.
    private void ApplyContentMinWidth(WindowToken t, bool force)
    {
        if (!t.Resizable || t.Rect == null || !t.MeasureMinWidth(force)) return;   // false = no change / nothing marked
        var min = t.EffectiveMinWidth;
        _log.Info($"[Window] {t.WindowId} min width from content: {min:0}");   // only on change (rare)
        if (_ticker != null)
            for (var i = 0; i < _ticker.DragResizers.Count; i++)
            {
                var d = _ticker.DragResizers[i];
                if (d.Target != t.Rect) continue;
                _ticker.DragResizers[i] = (d.Grip, d.Target, new Vector2(min, d.Min.y), d.Max, d.WindowId, d.EditOnly);
                break;
            }
        // Hysteresis: widen only past half a unit (sub-pixel layout noise must never nudge the window).
        var cur = GetRect(t);
        // Slide against the CURRENT width here: SetRect's own on-screen guard compares against the width it was asked
        // for, which is already `min` on this path, so it would not slide.
        if (min > cur.Width + 0.5f) SetRect(t, KeepWidenedOnScreen(new WindowRect(cur.X, cur.Y, min, cur.Height), cur.Width));
    }

    // A widen grows the window RIGHTWARD from its top-left (as the resize grip does). If the window was fully on-screen
    // at the width it asked for but the min-clamp pushed its right edge off-screen, slide it left so it still fits
    // (never past the left edge). ClampVisible alone only keeps a grab band visible, which would leave the widened
    // part — the Send button / last tabs — off-screen. Canvas units throughout (bound = Screen ÷ scaleFactor).
    private WindowRect KeepWidenedOnScreen(WindowRect rect, float requestedWidth)
    {
        if (rect.Width <= requestedWidth + 0.5f) return rect;   // not widened by the clamp
        var s = _canvasComp != null && _canvasComp.scaleFactor > 0f ? _canvasComp.scaleFactor : 1f;
        var screenW = Screen.width / s;
        if (rect.X + requestedWidth > screenW + 0.5f || rect.X + rect.Width <= screenW) return rect;
        return new WindowRect(Mathf.Max(0f, screenW - rect.Width), rect.Y, rect.Width, rect.Height);
    }
}
