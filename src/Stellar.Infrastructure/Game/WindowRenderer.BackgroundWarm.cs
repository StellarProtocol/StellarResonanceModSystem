using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using HitchProbe = Stellar.Abstractions.Diagnostics.HitchProbe;
using WindowToken = Stellar.Infrastructure.Game.WindowBuilder.WindowToken;

namespace Stellar.Infrastructure.Game;

// BACKGROUND WARM (IWindowBackgroundWarm, driven by the Host's WindowPrewarm). The pre-warm used to mount a window with
// ShouldRender false and park it a few ticks later. Its root then sat INACTIVE until the first real open, and Unity lays
// out nothing under an inactive root — so everything a mount leaves for later landed right after that open, measured
// 11-09 on the Settings hub (the first open of a pre-warmed hub):
//   • the open frame: apply 12.1 ms = 24 bubble "settle" measures owed by the mount apply + the forced first layout;
//   • the next frames: the background layout of the 7 pre-built tabs, which only runs while the window is on screen —
//     their first applies (57 bubble measures + a whole-window structural rebuild: apply 53.7 ms, canvas 14.3 ms) and
//     the Performance tab's one-unit reveal (10.7 ms) → 79.6 / 89.5 / 64.4 ms frames.
// Now the warm keeps the root ACTIVE but 100000 canvas units to the right (StageOffset: never on screen, never under
// the pointer, so every ticker hit-test misses it), applied like a shown window, until BackgroundWarmRules.IsDone —
// every prebuilt branch built, laid out and parked, every bubble settled. Then it parks as before, and the first real
// open is a plain re-show of a fully warm window (its parked tabs re-warm with their bindings off, .KeepAlive.cs).
// A window with KeepAlive tabs and its own canvas SOFT-parks instead (.SoftPark.cs): it stays active right here
// off-screen with its canvas off, so its first open is a canvas enable and nothing re-warms.
//
// The shift is invisible to everyone else: GetRect/SetRect speak the window's REAL position (so WindowService never
// persists or clamps the shifted one), and the sticky cursor ignores the root (WindowInteractionTicker.BackgroundRoots —
// an active interactive root would count as "a Stellar window is open"). Not BringToFront'ed, no z change.
internal sealed partial class WindowRenderer : IWindowBackgroundWarm
{
    private const float BackgroundOffset = 100000f;
    private readonly HashSet<string> _bgWarm = new();

    public void SetBackgroundWarm(string windowId, bool on)
    {
        if (on) { _bgWarm.Add(windowId); return; }
        if (!_bgWarm.Contains(windowId)) return;
        if (HitchProbe.IsEnabled && Find(windowId) is { } t)
            _log.Info($"[Prebuild] {windowId}: background warm {(IsBackgroundWarmDone(windowId) ? "done" : "cut short")} — "
                + $"{t.ApplyCount - t.BgWarmStartApply} applies over {Time.frameCount - t.BgWarmStartFrame} frames");
        _bgWarm.Remove(windowId);   // moved back home by its next apply / park (ApplyValues, Park)
    }

    public bool IsBackgroundWarmDone(string windowId)
        => Find(windowId) is { } t && t.Root != null && t.BgShifted
           && BackgroundWarmRules.IsDone(t.Prebuild == null, t.ApplyCount - t.BgWarmStartApply, t.BackgroundMeasurePending);

    private WindowToken? Find(string windowId)
    {
        for (var i = 0; i < _tokens.Count; i++) if (_tokens[i].WindowId == windowId) return _tokens[i];
        return null;
    }

    private bool InBackgroundWarm(WindowToken t) => _bgWarm.Count > 0 && _bgWarm.Contains(t.WindowId);

    // Root (de)activation for one apply. Returns the [Hitch] start of a REAL show (from hidden, or the first one; 0 =
    // none or the probe is off). bg = background warm: shifted off-screen BEFORE it (re)activates, so it never draws a
    // frame on screen.
    private long SetShown(WindowToken t, bool hideAll, bool bg)
    {
        // Soft-parked (.SoftPark.cs): hidden by its gate stays parked as it is; shown = canvas on, nothing re-enabled.
        var plan = SoftParkRules.PlanShow(t.SoftParked, t.Root.activeSelf, hideAll);
        if (plan.StaySoftParked) return 0L;
        if (bg && !t.BgShifted) ShiftOff(t);
        else if (bg && plan.SoftUnpark) RestartWarmCount(t);   // a re-warm of a soft-parked window: already off-screen
        var realShow = !hideAll && !bg && (plan.WasHidden || !t.EverShown);
        var showT = realShow ? HitchProbe.Begin() : 0L;
        if (realShow) { t.ShowIsFirst = !t.EverShown; t.EverShown = true; }
        if (plan.SoftUnpark) { SoftUnpark(t); _ticker?.MarkUiGeometryDirty(); }   // shown again: hover rescan
        // Still inactive: parked KeepAlive branches drop back to background re-warming, or the re-activation below would
        // re-enable (re-mesh + re-layout) every one of them in this frame (WindowBuilder.KeepAlive.cs).
        if (plan.PrepareReshow) t.PrepareReshow();
        if (t.Root.activeSelf == hideAll) { t.Root.SetActive(!hideAll); _ticker?.MarkUiGeometryDirty(); }   // shown / ShouldRender-hidden
        if (!bg && t.BgShifted) ShiftBack(t);   // warm / soft park over: back home (a real show, or about to park)
        // A window re-shown after being deactivated re-arms its immediate first-layout, so a content-sized popup
        // (e.g. the cursor-positioned row context menu) is rebuilt to its true size the same frame it reappears
        // — otherwise it shows one frame at the previous open's size, which mis-clamps its on-screen position.
        // A soft-parked window stayed laid out: no forced whole-window rebuild on its open.
        if (plan.ResetLayout) t.ResetLayout();
        return showT;
    }

    // [Hitch] firstshow:<id> / show:<id> (activation + apply) and a [UiPerf] winshow line with the frame's canvas rebuild.
    private static void NoteShow(WindowToken t, long showT, long applyT)
    {
        t.ShowLabels ??= new[] { "show:" + t.WindowId, "firstshow:" + t.WindowId };
        HitchProbe.End(t.ShowLabels[t.ShowIsFirst ? 1 : 0], showT);
        var now = Stopwatch.GetTimestamp();
        UiPerfStats.WindowShow(t.WindowId, t.ShowIsFirst, applyT - showT, now - applyT);
    }

    private static void RestartWarmCount(WindowToken t) { t.BgWarmStartApply = t.ApplyCount; t.BgWarmStartFrame = Time.frameCount; }

    private void ShiftOff(WindowToken t)
    {
        t.BgShifted = true; RestartWarmCount(t);
        t.Rect.anchoredPosition += new Vector2(BackgroundOffset, 0f);
        _ticker?.BackgroundRoots.Add(t.Rect);
        _ticker?.MarkUiGeometryDirty();
    }

    private void ShiftBack(WindowToken t)
    {
        t.BgShifted = false;
        if (t.Rect == null) return;
        t.Rect.anchoredPosition -= new Vector2(BackgroundOffset, 0f);
        _ticker?.BackgroundRoots.Remove(t.Rect);
        _ticker?.MarkUiGeometryDirty();
    }

    // GetRect/SetRect speak the REAL position while a window sits shifted.
    private static float ShiftX(WindowToken t) => t.BgShifted ? BackgroundOffset : 0f;
}

internal sealed partial class WindowBuilder
{
    public sealed partial class WindowToken   // the renderer's background-warm / show bookkeeping (WindowRenderer.BackgroundWarm.cs)
    {
        internal bool BgShifted;                       // sitting BackgroundOffset units to the right for a background warm
        internal int BgWarmStartApply, BgWarmStartFrame;
        internal bool EverShown, ShowIsFirst;          // a REAL show happened / the one being measured is the first
        internal string[]? ShowLabels;                 // [Hitch] bucket names, built on first show
    }
}
