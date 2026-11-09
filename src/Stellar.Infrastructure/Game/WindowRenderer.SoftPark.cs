using System.Diagnostics;
using Stellar.Application.Abstractions;
using UnityEngine;
using UnityEngine.UI;
using HitchProbe = Stellar.Abstractions.Diagnostics.HitchProbe;
using WindowToken = Stellar.Infrastructure.Game.WindowBuilder.WindowToken;

namespace Stellar.Infrastructure.Game;

// SOFT PARK: hide a window WITHOUT deactivating its root. Measured 11-09 on the Settings hub (every ✕):
// [Hitch] frame≈75-79 ms with svc:window=28.7-29.8 ms + ui:canvasRebuild 9 ms — the root's SetActive(false) ran OnDisable
// on every component of every KeepAlive tab (they stay ACTIVE while parked, ~1900 units), and the next open paid the mirror
// image: PrepareReshow switched those units off while the root was inactive, the root's SetActive(true) OnEnabled the
// visible tab + chrome (show 6-12 ms, canvasRebuild 8-16 ms), and then every tab re-warmed unit by unit over ~34 frames.
//
// A window with its own canvas and KeepAlive branches (SoftParkRules.CanSoftPark) now parks like a KeepAlive tab does:
// its own Canvas + GraphicRaycaster off (Unity keeps the meshes and batches, no OnDisable), moved BackgroundOffset units
// to the right (the ticker hit-tests rects and ignores canvas state, so an invisible copy must never sit under the
// pointer; FrontWindowBlocks / sticky cursor / scroll / hover all miss it, and BackgroundRoots keeps the sticky cursor
// from counting it as open), text-field focus and the EventSystem selection released. Showing it again = canvas on +
// move home: no OnEnable, no layout reset, no re-warm. GetRect/SetRect still speak its real position (ShiftX).
//
// While hidden it is re-applied in the background (IWindowParkedRefresh, ~2/s, bubble measures budgeted): its bindings
// stay current, so a first open after world entry no longer re-pulls (and re-measures) what changed since the warm
// (apply 21.7 ms vs 3 ms on later opens, 11-09), and a parked tab's ContentRevision refresh runs while hidden.
// Steady cost while hidden: that apply (the visible tab's bindings only — parked tabs are gated) and uGUI's per-frame
// clip pass / ScrollRect LateUpdate over an active tree; off-screen, RectMask2D content is culled, so a font-atlas
// rebuild does not re-mesh it.
internal sealed partial class WindowRenderer : IWindowParkedRefresh
{
    private bool TrySoftPark(WindowToken t)
    {
        if (!SoftParkRules.CanSoftPark(t.Root != null && t.Root.activeSelf, t.OwnCanvas != null, t.KeepAlives.Count, t.ZPopup))
            return false;
        t.DefocusFieldsUnder(t.Root!.transform);   // SetActive(false) used to end a focused field's edit
        ReleaseSelectionUnder(t.Root.transform);
        SetOwnCanvases(t, false);
        if (!t.BgShifted) ShiftOff(t);   // a background warm ending here is already off-screen: stays there
        t.SoftParked = true;
        t.ParkedTicks = 0;
        return true;
    }

    // Shown again (real show or a background re-warm): drawable and hittable; SetShown moves it home for a real show.
    private static void SoftUnpark(WindowToken t)
    {
        t.SoftParked = false;
        SetOwnCanvases(t, true);
    }

    // The window's own canvas, plus each SHOWN KeepAlive tab's (a parked tab's canvas is already off and stays off).
    private static void SetOwnCanvases(WindowToken t, bool on)
    {
        if (t.OwnCanvas != null && t.OwnCanvas.enabled != on) t.OwnCanvas.enabled = on;
        if (t.OwnRaycaster != null && t.OwnRaycaster.enabled != on) t.OwnRaycaster.enabled = on;
        for (var i = 0; i < t.KeepAlives.Count; i++)
        {
            var h = t.KeepAlives[i];
            if (h.State != KeepAliveState.Shown || h.Canvas == null) continue;
            if (h.Canvas.enabled != on) h.Canvas.enabled = on;
            if (h.Raycaster != null && h.Raycaster.enabled != on) h.Raycaster.enabled = on;
        }
    }

    // A Button clears the selection after its click, but anything else left selected under a hidden-but-active window
    // would still receive Submit/keyboard navigation from the EventSystem (Selectable.IsActive is true).
    private static void ReleaseSelectionUnder(Transform root)
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        var sel = es != null ? es.currentSelectedGameObject : null;
        if (sel != null && sel.transform.IsChildOf(root)) es!.SetSelectedGameObject(null);
    }

    public bool WantsParkedRefresh(object? token)
    {
        if (token is not WindowToken t || !t.SoftParked || t.Root == null) return false;
        return SoftParkRules.ShouldRefresh(++t.ParkedTicks, HasBackgroundWork(t));
    }

    public void RefreshParked(object? token)
    {
        if (token is not WindowToken t || !t.SoftParked || t.Root == null) return;
        WindowBuilder.BubbleBinding.WindowInBackground = true;   // off-screen: every bubble measure is budgeted
        try { t.Apply(); } finally { WindowBuilder.BubbleBinding.WindowInBackground = false; }
    }

    private static bool HasBackgroundWork(WindowToken t)
    {
        if (t.BackgroundMeasurePending > 0 || t.Prebuild != null) return true;
        for (var i = 0; i < t.KeepAlives.Count; i++) if (t.KeepAlives[i].Gate.Refreshing) return true;
        return false;
    }

    // [Hitch] winhide:<id> (the park itself) and a [UiPerf] winhide line with the next frame's canvas rebuild — only for a
    // window that was really shown (a background warm's own park is not a close).
    private static void NoteHide(WindowToken t, long hideT, bool soft)
    {
        if (hideT == 0L || !t.EverShown) return;
        t.HideLabel ??= "winhide:" + t.WindowId;
        HitchProbe.End(t.HideLabel, hideT);
        UiPerfStats.WindowHide(t.WindowId, soft, Stopwatch.GetTimestamp() - hideT);
    }
}

internal sealed partial class WindowBuilder
{
    public sealed partial class WindowToken   // the renderer's soft-park bookkeeping (WindowRenderer.SoftPark.cs)
    {
        internal Canvas? OwnCanvas;               // the window root's own nested canvas (AttachNestedCanvas), if any
        internal GraphicRaycaster? OwnRaycaster;
        internal bool SoftParked;                 // hidden with its root still active (canvas off, off-screen)
        internal int ParkedTicks;                 // capped apply ticks since the soft park (background refresh cadence)
        internal string? HideLabel;               // [Hitch] bucket name, built on first hide
    }
}
