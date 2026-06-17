using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

internal sealed partial class HudService
{
    private LayoutStorage? _storage;
    private Func<Resolution>? _resolution;
    private readonly HashSet<string> _dragging = new();

    public void AttachLayout(LayoutStorage storage, Func<Resolution> resolution) { _storage = storage; _resolution = resolution; }

    public WindowRect GetRect(string id) => _huds.TryGetValue(id, out var e) && e.Token != null ? _renderer.GetRect(e.Token) : default;
    public void SetRect(string id, WindowRect rect) { if (_huds.TryGetValue(id, out var e) && e.Token != null) _renderer.SetRect(e.Token, rect); }
    public void BeginDrag(string id) => _dragging.Add(id);
    public void EndDrag(string id) => _dragging.Remove(id);

    public void CommitRect(string id)
    {
        if (_storage is null || _resolution is null || !_huds.TryGetValue(id, out var e) || e.Token is null) return;
        _storage.Save(_storage.ActiveSlot, id, _resolution(), _renderer.GetRect(e.Token), visible: true);
    }

    public IEnumerable<(string Id, WindowRect Rect)> ShownRects()
    {
        foreach (var e in _huds.Values)
            if (!e.Removed && e.Visible && e.Token != null) yield return (e.Spec.Id, _renderer.GetRect(e.Token));
    }

    /// <summary>Editor-driven visibility toggle for a mod HUD: flip Visible (TickEntry mounts/destroys to match)
    /// and persist it per slot so it survives a relaunch.</summary>
    public void SetVisiblePersist(string id, bool visible)
    {
        if (!_huds.TryGetValue(id, out var e)) return;
        e.SetVisible(visible);
        if (_storage != null && _resolution != null)
            _storage.Save(_storage.ActiveSlot, e.Spec.Id, _resolution(), RectForPersist(e), visible);
    }

    /// <summary>Editor enumeration — all registered HUDs incl. hidden, with live rect when shown else last-known
    /// (or the spec default). Mod HUDs are always hideable.</summary>
    public IEnumerable<EditableElement> EditableElements()
    {
        foreach (var e in _huds.Values)
        {
            if (e.Removed) continue;
            yield return new EditableElement(e.Spec.Id, RectForPersist(e), e.Visible, CanHide: true);
        }
    }

    private WindowRect RectForPersist(Entry e)
        => e.Token != null ? _renderer.GetRect(e.Token)
         : e.LastShownRect.Width > 0 ? e.LastShownRect
         : e.Spec.DynamicDefaultRect?.Invoke() ?? e.Spec.DefaultRect ?? default;

    /// <summary>Layout-editor "Reset" for a mod HUD: drop its saved override and re-place it at the
    /// (on-screen-clamped) DefaultRect / renderer-initial position. No-ops if the id isn't a mounted HUD.</summary>
    public void ResetRect(string id)
    {
        if (_storage is null || _resolution is null || !_huds.TryGetValue(id, out var e)) return;
        e.SetVisible(true);                          // reset restores default visibility (shown)
        _storage.Remove(_storage.ActiveSlot, id);
        if (e.Token != null) ApplySavedRect(e);      // re-place if mounted; a hidden one mounts+places next tick
    }

    // Called after a successful mount (Task 4 TickEntry). Restores saved position — unless a drag is
    // active for this id (so a mid-drag self-heal re-mount doesn't discard the live drag).
    private void ApplySavedRect(Entry e)
    {
        if (_storage is null || _resolution is null || e.Token is null || _dragging.Contains(e.Spec.Id)) return;
        // Fall back to the spec's DefaultRect (spawn position) when set, else the renderer's initial placement.
        var fallback = e.Spec.DynamicDefaultRect?.Invoke() ?? e.Spec.DefaultRect ?? _renderer.GetRect(e.Token);
        var (rect, visible) = _storage.Get(_storage.ActiveSlot, e.Spec.Id, _resolution(), fallback);
        _renderer.SetRect(e.Token, rect);
        if (!visible) e.SetVisible(false);   // honour a persisted hide (TickEntry destroys next tick)
    }
}
