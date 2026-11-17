using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Plugin <see cref="Stellar.Abstractions.Services.IWindowControl.Rect"/> / <c>SetRect</c> while a window's saved-layout
/// restore is still PARKED (<see cref="Entry.ApplyPending"/>: mounted on a fresh canvas at login / scene change /
/// a res change, before the CanvasScaler settled — see <c>ApplySavedRect</c>).
/// <para>The bug this closes (fw 2.33.0, CombatMeter report 2026-11-17): the window is already <c>IsShown</c> in that gap
/// but still sits at the mount placeholder (20,20) — <c>Rect</c> returned it, and the plugin's "keep position, change
/// height" <c>SetRect(r.X, r.Y, r.W, newH)</c> PERSISTED it at once; the deferred restore then read back the just-saved
/// (20,20). The window moved to the top-left for good.</para>
/// <para>Semantics while the restore is pending:</para>
/// <list type="bullet">
/// <item><c>Rect</c> reports where the restore WILL put the window (saved rect, else the anchored default) — merged with
/// any request the plugin already made — never the placeholder.</item>
/// <item><c>SetRect</c> never persists. It is queued, and its size is applied to the live window at once. When the restore
/// lands, the queued request is re-issued through the normal (persisting) path:
///   an ECHO request (its X/Y = what <c>Rect</c> reported, or the live placeholder) keeps the RESTORED position and takes
///   only the requested size — the "resize in place" case; a request with a different X/Y is an explicit MOVE and wins
///   in full (e.g. a plugin centring a just-shown child).</item>
/// </list>
/// Outside the pending window nothing changes: <c>Rect</c>/<c>SetRect</c> behave exactly as before.
/// </summary>
internal sealed partial class WindowService
{
    // The rect ApplySavedRect places a window at: the saved rect for the active slot / global bucket + resolution (else
    // the anchored DefaultRect), re-placed from its anchor offset. Shared by the real apply and the pending preview, so
    // the preview can never drift from what the restore does. (While pending, CanvasScale may still be the transient
    // default, so the preview's on-screen clamp can differ slightly from the final one — echo detection compares against
    // the PREVIEW the plugin was shown, and the final position is always re-taken from the real restore.)
    private WindowRect ComputeRestoredRect(Entry e, out bool visible)
    {
        var fallback = ResolveAnchoredDefault(e.Reg.Spec);
        visible = true;
        if (_storage is null || _resolution is null) return fallback;
        var (rect, vis) = StorageGet(e.Reg.Spec, _resolution(), fallback, CanvasScale);
        visible = vis;
        return Reanchor(e.Reg.Spec, rect);   // 2.22.0: anchored windows re-placed from their anchor offset
    }

    // What Rect reports while the restore is parked: the plugin's own queued request if it made one, else the restore
    // target (computed lazily, once per pending window — Rect may be read every plugin tick).
    private WindowRect PendingPreviewRect(Entry e)
    {
        if (e.PendingPluginRect is { } req) return req;
        return e.PendingRestorePreview ??= ComputeRestoredRect(e, out _);
    }

    // Entry.SetRect while ApplyPending: queue, never persist (persisting here IS the top-left bug).
    private void QueuePendingSetRect(Entry e, WindowRect rect)
    {
        var shown = PendingPreviewRect(e);
        var live = _renderer.GetRect(e.Token);
        // Echo = the plugin wrote back a position it read from us (the preview), or the raw placeholder (a Rect read
        // that predates this fix's preview, e.g. cached by the plugin). Either way it never MEANT that position.
        var echo = PosClose(rect, shown) || PosClose(rect, live);
        if (echo)
        {
            // An echo after an explicit move keeps that move (the preview it echoed IS the move).
            e.PendingPluginRect = new WindowRect(shown.X, shown.Y, rect.Width, rect.Height);
            _renderer.SetRect(e.Token, new WindowRect(live.X, live.Y, rect.Width, rect.Height));   // size now, position at restore
        }
        else
        {
            e.PendingPluginRect = rect;
            e.PendingPluginMoves = true;
            _renderer.SetRect(e.Token, rect);   // interim only (not persisted); the restore re-issues it
        }
    }

    // Called by ApplySavedRect right after the real restore: re-issue a request queued during the pending window through
    // the normal persisting Entry.SetRect (pending is cleared by now). Echo → restored X/Y + requested size; move → as asked.
    private static void ConsumePendingSetRect(Entry e, WindowRect restored)
    {
        e.PendingRestorePreview = null;
        if (e.PendingPluginRect is not { } req) return;
        var moves = e.PendingPluginMoves;
        e.PendingPluginRect = null; e.PendingPluginMoves = false;
        e.SetRect(moves ? req : new WindowRect(restored.X, restored.Y, req.Width, req.Height));
    }

    private static bool PosClose(WindowRect a, WindowRect b)
        => Math.Abs(a.X - b.X) < 0.5f && Math.Abs(a.Y - b.Y) < 0.5f;

    private sealed partial class Entry
    {
        // Set while ApplyPending (see the class summary): the plugin's queued SetRect (already merged with the preview's
        // position when it was an echo), whether it was an explicit move, and the cached restore-target preview.
        public WindowRect? PendingPluginRect; public bool PendingPluginMoves; public WindowRect? PendingRestorePreview;
    }
}
