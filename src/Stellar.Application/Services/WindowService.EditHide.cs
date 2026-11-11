using System;

namespace Stellar.Application.Services;

/// <summary>
/// Layout-editor "Hide others": a TRANSIENT draw-suppress for edit-mode windows (<c>EditModeDragOnly</c> — exactly the
/// set the editor outlines). It rides the same hide path as a false <c>ShouldRender</c> (the renderer deactivates the
/// root, no value pulls), so it never touches <c>Visible</c>, never persists, never reshuffles stacking, and clearing
/// it shows the window again only if the user had it shown. Framework-internal; no SDK surface.
/// </summary>
internal sealed partial class WindowService
{
    /// <summary>
    /// Set the transient hide on every edit-mode window for which <paramref name="hide"/> returns true (null = clear
    /// all). Idempotent and cheap when nothing changes (a flag compare per window; a change marks the window dirty so
    /// it repaints this tick). Returns how many windows are hidden by it that the user had shown.
    /// </summary>
    internal int SyncEditHidden(Func<string, bool>? hide)
    {
        var count = 0;
        foreach (var e in _windows.Values)
        {
            if (e.Removed) continue;
            var h = hide != null && e.Reg.Spec.EditModeDragOnly && hide(e.Reg.Spec.Id);
            if (e.EditHidden != h) { e.EditHidden = h; e.Dirty = true; }
            if (h && e.Visible) count++;
        }
        return count;
    }

    /// <summary>True while the layout editor's "Hide others" suppresses <paramref name="id"/>.</summary>
    internal bool IsEditHidden(string id) => _windows.TryGetValue(id, out var e) && !e.Removed && e.EditHidden;
}
