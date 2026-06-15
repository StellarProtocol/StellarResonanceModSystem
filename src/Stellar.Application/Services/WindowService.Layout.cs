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
                     : e.LastSavedRect.Width > 0 ? e.LastSavedRect : e.Reg.Spec.DefaultRect;
            _storage.Save(_storage.ActiveSlot, id, _resolution(), rect, visible);
        }
    }

    // Restore the saved rect (or DefaultRect) right after a successful mount.
    private void ApplySavedRect(Entry e)
    {
        if (e.Token is null) return;
        var fallback = e.Reg.Spec.DefaultRect;
        if (_storage is null || _resolution is null)
        {
            _renderer.SetRect(e.Token, fallback);
            e.LastRect = e.LastSavedRect = fallback;
            return;
        }
        var (rect, visible) = _storage.Get(_storage.ActiveSlot, e.Reg.Spec.Id, _resolution(), fallback);
        _renderer.SetRect(e.Token, rect);
        e.LastRect = e.LastSavedRect = rect;
        if (!visible) e.SetVisible(false);   // honour a persisted hide (TickEntry destroys next tick)
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
