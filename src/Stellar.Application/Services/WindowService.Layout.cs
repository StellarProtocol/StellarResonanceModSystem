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
        // Re-attach safety: drop a prior storage's subscription first so the handler neither leaks nor
        // double-applies on a slot change if AttachLayout is ever called more than once.
        if (_storage is not null) _storage.SlotsChanged -= ReapplyForSlotChange;
        _storage = storage;
        _resolution = resolution;
        // A layout slot captures the whole screen arrangement, so a slot switch must re-place the mod windows
        // from the now-active slot (SlotsChanged fires on SetActiveSlot, and on an add/remove that shifts the
        // active slot). This is the single source of truth — every entry point (toolbar + Settings→Layout panel)
        // routes through SetActiveSlot, so neither call site needs its own reapply.
        _storage.SlotsChanged += ReapplyForSlotChange;
    }

    // SlotsChanged handler: re-place the arrangeable HUDs from the active slot AND apply their saved visibility
    // (applyVisibility:true) — a slot switch is an explicit user action, like the old toolbar native-reapply call.
    // ReapplyLayout only READS _storage.Get downstream (never mutates the slot list), so it can't re-fire
    // SlotsChanged — no feedback loop. Rename / add / remove on the SAME active slot re-read the same slot = no
    // visible change; a remove that shifts the active slot correctly follows to the new one.
    // editableHudsOnly:true — slots ARRANGE ONLY the arrangeable HUD set (the EditModeDragOnly windows the layout
    // editor manages). Non-HUD free-drag windows (Settings / DataInspector / launcher / CombatMeter History) are
    // NOT part of any slot's arrangement, so a slot switch must leave BOTH their position AND their visibility
    // untouched — this gate skips them. The resolution-change reapply (ServiceTick) leaves this false so ALL
    // windows still re-clamp from their bucket on a res/scale change (a dialog must not land off-screen).
    // skipSlotIndependent:true — kept for clarity/safety: a slot-independent window's layout does NOT belong to any
    // slot either (the toolbar is also non-EditModeDragOnly, so the HUD gate already covers it; both conditions stay).
    private void ReapplyForSlotChange() => ReapplyLayout(applyVisibility: true, skipSlotIndependent: true, editableHudsOnly: true);

    // A window uses the GLOBAL (slot-independent) layout bucket when it opts in explicitly (framework chrome via
    // SlotIndependentLayout) OR when it is a non-HUD free-drag window (!EditModeDragOnly). Slots arrange ONLY the
    // EditModeDragOnly HUD set, so everything else — Settings / DataInspector / launcher / CombatMeter History —
    // keeps ONE position/visibility across all slots instead of a per-slot copy.
    private static bool UsesGlobalLayout(WindowSpec spec) => spec.SlotIndependentLayout || !spec.EditModeDragOnly;

    // Route a window's layout read/write to the slot-INDEPENDENT global bucket for a slot-independent OR non-HUD
    // window (see UsesGlobalLayout), else to the active slot. One dispatch point per storage op so no read/write
    // path can silently drift back to slot-keyed (the coverage trap). Every caller has already null-checked
    // _storage / _resolution, so the `!` suppressions are sound.
    private (WindowRect Rect, bool Visible) StorageGet(WindowSpec spec, Resolution res, WindowRect fallback, float scale)
        => UsesGlobalLayout(spec)
            ? _storage!.GetGlobal(spec.Id, res, fallback, scale)
            : _storage!.Get(_storage.ActiveSlot, spec.Id, res, fallback, scale);

    private void StorageSave(WindowSpec spec, Resolution res, LayoutStorage.WindowState state)
    {
        if (UsesGlobalLayout(spec)) _storage!.SaveGlobal(spec.Id, res, state);
        else _storage!.Save(_storage!.ActiveSlot, spec.Id, res, state);
    }

    private void StorageSave(WindowSpec spec, Resolution res, WindowRect rect, bool visible)
    {
        if (UsesGlobalLayout(spec)) _storage!.SaveGlobal(spec.Id, res, rect, visible);
        else _storage!.Save(_storage!.ActiveSlot, spec.Id, res, rect, visible);
    }

    private void StorageRemove(WindowSpec spec)
    {
        if (UsesGlobalLayout(spec)) _storage!.RemoveGlobal(spec.Id);
        else _storage!.Remove(_storage!.ActiveSlot, spec.Id);
    }

    private bool StorageIsPersistedHidden(WindowSpec spec, Resolution res)
        => UsesGlobalLayout(spec)
            ? _storage!.IsPersistedHiddenGlobal(spec.Id, res)
            : _storage!.IsPersistedHidden(_storage.ActiveSlot, spec.Id, res);

    private bool StorageTryGetAnchorOffset(WindowSpec spec, Resolution res, out (float X, float Y) offset, out bool legacyExact)
        => UsesGlobalLayout(spec)
            ? _storage!.TryGetAnchorOffsetGlobal(spec.Id, res, out offset, out legacyExact)
            : _storage!.TryGetAnchorOffset(_storage.ActiveSlot, spec.Id, res, out offset, out legacyExact);

    /// <summary>Layout-editor "Reset" for a mod window: drop its saved override and re-place it at the
    /// (on-screen-clamped) DefaultRect. No-ops if the id isn't a registered/mounted window, so the editor can
    /// fan a reset across all element services without knowing which owns the id.</summary>
    public void ResetRect(string id)
    {
        if (!_windows.TryGetValue(id, out var e)) return;
        e.SetVisible(true);                          // reset restores default visibility (shown)
        if (_storage != null) StorageRemove(e.Reg.Spec);
        if (e.Token != null) ApplySavedRect(e);      // re-place if mounted; a hidden one mounts+places next tick
    }

    /// <summary>Layout-editor arrow-key nudge for a mod window: shift its position by a screen-px delta. No-op if
    /// the id isn't a mounted window. The delta is converted screen-px → CANVAS UNITS (the renderer's GetRect/SetRect
    /// domain, = screen ÷ <see cref="CanvasScale"/>) and the result is clamped so ≥ ~24 screen-px of the window stays
    /// on-screen.
    /// <para>Persistence mirrors a mouse drag EXACTLY: it moves the live rect through the renderer's NON-persisting
    /// <see cref="IWindowRenderer.SetRect"/> and deliberately does NOT touch <see cref="Entry.LastRect"/>. PersistIfSettled
    /// compares the live rect to the PREVIOUS ~10 Hz tick's LastRect to detect a settle and saves once when movement
    /// stops — so a held arrow writes to disk only on release, never per tick. (Writing LastRect here each tick would
    /// make every apply look "settled" and disk-write on every held-arrow tick.) Every nudgeable window is
    /// EditModeDragOnly ⇒ Draggable, so the PersistIfSettled gate (<c>Draggable || Resizable</c>) always fires.</para></summary>
    internal void NudgeRect(string id, float dxPx, float dyPx)
    {
        if (!_windows.TryGetValue(id, out var e) || e.Token == null) return;
        var sf = CanvasScale <= 0f ? 1f : CanvasScale;
        var cur = _renderer.GetRect(e.Token);
        float nx = cur.X + dxPx / sf;   // screen-px delta → canvas units
        float ny = cur.Y + dyPx / sf;
        // Keep ≥ 24 screen-px (→ canvas units) of the window on-screen; canvas-unit screen bound = resolution ÷ scale
        // (same domain as ApplySavedRect's clamp). Manual min/max (never throws when the min bound exceeds the max,
        // unlike Math.Clamp) — a window wider than the screen keeps a definite placement.
        var res = _resolution?.Invoke() ?? default;
        if (res.Width > 0 && res.Height > 0)
        {
            float m = 24f / sf;
            float cw = res.Width / sf, ch = res.Height / sf;
            nx = Math.Max(m - cur.Width, Math.Min(cw - m, nx));
            ny = Math.Max(m - cur.Height, Math.Min(ch - m, ny));
        }
        _renderer.SetRect(e.Token, new WindowRect(nx, ny, cur.Width, cur.Height));
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
            SaveRect(e.Reg.Spec, rect, visible);
        }
    }

    // Reload the saved layout for the CURRENT resolution for every mounted window. ApplySavedRect reads
    // _resolution() + _storage.Get, so it picks up the current resolution's bucket (or the anchor-resolved
    // DefaultRect fallback) and SetRect clamps it on-screen. Two callers:
    //   • resolution change (ServiceTick) → applyVisibility:false, editableHudsOnly:false (the DEFAULTS): reposition
    //     ALL windows (dialogs included, so none lands off-screen), never toggle show/hide (the game owns show/hide
    //     during a transition, and a res change isn't a user layout action).
    //   • slot switch (ReapplyForSlotChange) → applyVisibility:true, editableHudsOnly:true: a slot ARRANGES ONLY the
    //     arrangeable HUDs (EditModeDragOnly), carrying their own saved show/hide, so honour the new slot's position
    //     + persisted visibility for THOSE — and leave non-HUD free-drag windows entirely alone.
    internal void ReapplyLayout(bool applyVisibility = false, bool skipSlotIndependent = false, bool editableHudsOnly = false)
    {
        foreach (var kv in _windows)
        {
            var e = kv.Value;
            if (e.Removed || e.Token == null) continue;
            // Slot-switch path passes skipSlotIndependent:true — a slot-independent window's layout isn't part of
            // any slot, so a slot change leaves it exactly where it is. The res-change path leaves this false.
            if (skipSlotIndependent && e.Reg.Spec.SlotIndependentLayout) continue;
            // Slot-switch path also passes editableHudsOnly:true — slots arrange ONLY the EditModeDragOnly HUD set,
            // so a non-HUD free-drag window (Settings/DataInspector/launcher/CombatMeter History) is skipped for BOTH
            // position and visibility. The res-change path leaves this false so every window still re-clamps.
            if (editableHudsOnly && !e.Reg.Spec.EditModeDragOnly) continue;
            ApplySavedRect(e, applyVisibility);
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
        // The saved-rect clamp (LayoutStorage.Get → ClampVisible) is scale-dependent: the on-screen bound is
        // resolution ÷ CanvasScale. On a canvas (re)create (boot / scene change) the freshly-added CanvasScaler
        // still reports the DEFAULT scaleFactor 1.0, so the bound is too small and a right/bottom window snaps
        // toward the origin. PARK the apply until the scaler settles (CanvasScaleReady) — TickEntry re-runs it then.
        // Meanwhile hold the last-known-good pose (it survives on the Entry across the remount), so no visible jump.
        if (!CanvasScaleReady)
        {
            // Sticky only WITHIN one pending window: a mount's applyVisibility=true must survive a reposition-only
            // (false) reapply that defers during the same canvas-settle wait. Once the apply runs (ApplyPending
            // cleared, below), the next cycle starts fresh — so a standalone resolution-change reapply on an
            // already-resolved window stays reposition-only and never re-hides a session-shown window.
            // Compute PendingApplyVisibility using the OLD ApplyPending, THEN set ApplyPending=true.
            e.PendingApplyVisibility = e.ApplyPending ? (e.PendingApplyVisibility || applyVisibility) : applyVisibility;
            e.ApplyPending = true;
            if (e.LastSavedRect.Width > 0f) _renderer.SetRect(e.Token, e.LastSavedRect);
            return;
        }
        e.ApplyPending = false;
        var fallback = ResolveAnchoredDefault(e.Reg.Spec);
        if (_storage is null || _resolution is null)
        {
            _renderer.SetRect(e.Token, fallback);
            e.LastRect = e.LastSavedRect = fallback;
            return;
        }
        var (rect, visible) = StorageGet(e.Reg.Spec, _resolution(), fallback, CanvasScale);
        rect = Reanchor(e.Reg.Spec, rect);   // 2.22.0: anchored windows re-placed from their anchor offset
        _renderer.SetRect(e.Token, rect);
        e.LastRect = e.LastSavedRect = rect;
        // Bidirectional on an applyVisibility apply (mount / reset / slot switch): restore BOTH directions, not
        // just hide. A slot switch carries its own persisted show/hide, so switching from a slot that hid this
        // window back to one where it's visible must SHOW it again — Get() returns visible=true for a slot with no
        // saved entry (or one marked shown), and SetVisible(true) is a no-op when already shown, so this never
        // force-renders (ShouldRender still gates the actual mount in TickEntry). The applyVisibility==false path
        // (resolution-change reapply) never reaches here — it repositions only and leaves current visibility.
        // NOT for click-away popups (DismissOnOutsideClick): they are transient and plugin-driven — they only mount
        // because the plugin just showed them. Their saved flag is whatever Entry.SetRect last persisted, usually
        // FALSE (a plugin places the parked popup with SetRect BEFORE SetVisible(true)), so restoring it re-hid the
        // popup the moment its first mount of the session landed: a one-frame flash at the old spot, then gone, with
        // the plugin still believing it open (ChatTools channel picker, 2026-10-24). A slot switch must not pop a
        // context menu open either, so the skip holds for every applyVisibility caller.
        if (applyVisibility && !e.Reg.Spec.DismissOnOutsideClick) e.SetVisible(visible);   // TickEntry mounts/destroys next tick to match
    }

    // Persist once after a drag settles: the rect is unchanged since last tick (drag stopped) AND differs
    // from what's saved. Avoids a disk write every frame during the drag.
    private void PersistIfSettled(Entry e)
    {
        if (_storage is null || _resolution is null || e.Token is null || e.ApplyPending
            || !(e.Reg.Spec.Draggable || e.Reg.Spec.Resizable)) return;   // never persist an interim/parked pose
        var cur = _renderer.GetRect(e.Token);
        if (RectClose(cur, e.LastRect) && !RectClose(cur, e.LastSavedRect))
        {
            SaveRect(e.Reg.Spec, cur, e.Visible);
            e.LastSavedRect = cur;
        }
        e.LastRect = cur;
    }

    // Position AND size (resizable windows persist their grip-dragged dimensions too).
    private static bool RectClose(WindowRect a, WindowRect b)
        => Math.Abs(a.X - b.X) < 0.5f && Math.Abs(a.Y - b.Y) < 0.5f
        && Math.Abs(a.Width - b.Width) < 0.5f && Math.Abs(a.Height - b.Height) < 0.5f;
}
