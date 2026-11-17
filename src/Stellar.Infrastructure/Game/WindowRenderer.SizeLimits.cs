using Stellar.Application.Abstractions;
using UnityEngine;
using WindowToken = Stellar.Infrastructure.Game.WindowBuilder.WindowToken;

namespace Stellar.Infrastructure.Game;

// Runtime resize limits (IWindowSizeLimits → WindowService.SizeLimits.cs). Like the content minimum width
// (.ContentMinWidth.cs) this adds no new enforcement path: it rewrites the ticker's DragResizers Min/Max for the window,
// which is what TickResize (grip, normal + layout-edit mode — the editor has no resize path of its own), SetRect (plugin
// set, saved-layout restore, slot switch) and ContentMinWidth's Min.x rewrite (keeps Min.y/Max) already clamp against.
internal sealed partial class WindowRenderer : IWindowResizeLimits
{
    public void SetResizeLimits(object? token, float minWidth, float minHeight, float maxWidth, float maxHeight)
    {
        if (token is not WindowToken t || !t.Resizable || t.Rect == null) return;
        // Width bounds feed the content minimum too: it is max(spec min, marked rows) capped at the max, so re-measure it
        // against the new bounds (cached layout reads only; a not-yet-laid-out row is skipped and the next apply catches up).
        t.SpecMinWidth = minWidth; t.SpecMaxWidth = maxWidth;
        t.EffectiveMinWidth = minWidth;
        t.MeasureMinWidth(force: true);
        var min = new Vector2(t.EffectiveMinWidth, minHeight);
        var max = new Vector2(maxWidth, maxHeight);
        if (_ticker != null)
            for (var i = 0; i < _ticker.DragResizers.Count; i++)
            {
                var d = _ticker.DragResizers[i];
                if (d.Target != t.Rect) continue;
                _ticker.DragResizers[i] = (d.Grip, d.Target, min, max, d.WindowId, d.EditOnly);
                break;
            }
        // Clamp the live size in place. sizeDelta only — the root is top-left anchored/pivoted (the grip grows it from
        // there), so the window never moves; the plugin's next SetRect (if any) does the on-screen clamp as usual.
        var s = t.Rect.sizeDelta;
        var c = new Vector2(Mathf.Clamp(s.x, min.x, max.x), Mathf.Clamp(s.y, min.y, max.y));
        if (c == s) return;
        t.Rect.sizeDelta = c;
        _ticker?.MarkUiGeometryDirty();   // real size change → hover rescan (as SetRect does)
    }
}
