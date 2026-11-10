using Stellar.Abstractions.Diagnostics;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

// WindowSpec.Prebuild — build a plugin window ahead of its first open, where a freeze doesn't matter (fw perf/prebuild,
// 2026-11-10). First opens in the 11-10 raid log: dungeonhub.confirm.raid 418 ms, dungeonhub.hub 204 ms, chattools.main
// 160 ms, combatmeter.main 121 ms — all mount cost (build:tree + build:firstApply), paid inside the open frame.
// Plugin ticks are IsWorldActive-gated, so the framework drives this from the un-gated window tick (Host:
// Wiring.Prewarm.cs PumpPluginPrebuilds → TickPrebuilds, right before Tick). Two stages:
//   1. STRUCTURE (login / character select, or a zone load): IWindowStructureMount builds the tree WITHOUT pulling a
//      value and parks it at once — no plugin value lambda runs before the world. One window per tick.
//   2. FILL (world up): a background warm (IWindowBackgroundWarm, the launcher/hub pre-warm's mechanism): the hidden
//      window is mounted/un-parked 100000 units off-screen, applied like a shown window until its work is done, then
//      parked. Behind the world-entry loading screen (which stays up for seconds after IsWorldActive rises) back to back;
//      otherwise during play, outside combat, PlayGapSeconds apart. One window at a time.
// The window stays hidden throughout: Visible is never touched (IsShown false, nothing persisted, no auto-focus), the
// fill only keeps it "live" for TickEntry (BgWarming). Off-screen + parked = no clicks or hover (every ticker hit-test
// misses an off-screen rect and skips inactive roots; the sticky cursor ignores BackgroundRoots).
// Stale content: a filled window whose PrebuildSignature moved while hidden is filled again (ContentWatch, the
// launcher's re-warm rule), and a new login into the world (InWorld rising edge) re-queues every fill.
// A real open mid-fill takes over (EndFillForRealShow): warm off, activated on top, moved home by its next apply.
// Also covered: a window that is Visible but held hidden by its ShouldRender and never drawn yet (a HUD that waits for a
// target / combat — targetlens.bufflist firstshow 74 ms, statinspector.mini-hud 45 ms in the 11-10 log). Hidden windows
// skip their apply, so its Conditionals and lists were built on the first DRAWN apply; the fill does that off-screen.
// Its takeover is ShouldRender turning true, not SetVisible.
internal sealed partial class WindowService
{
    private Entry? _filling;          // the one window being filled (off-screen), or null
    private int _fillTicks;
    private float _fillSeconds;
    private float _sinceFill = WindowPrebuildRules.PlayGapSeconds;   // the first fill in play needn't wait
    private float _sigClock;
    private bool _wasInWorld;

    /// <summary>Pump once per tick, right BEFORE <see cref="Tick"/> (un-gated: every phase), so a fill started, ended or
    /// aborted here is mounted / parked by the same tick's Tick — never one apply late (an abort must stop the plugin
    /// value pulls before the world goes down).</summary>
    internal void TickPrebuilds(PrebuildGate gate, float deltaSeconds)
    {
        if (gate.InWorld && !_wasInWorld)   // a new login into the world: character-specific content → fill again
            foreach (var e in _windows.Values) e.PrebuildFilled = false;
        _wasInWorld = gate.InWorld;
        if (_filling != null)
        {
            // World going down (zone load, logout) or canvas lost: stop pulling plugin values now; it is filled again later.
            if (gate.Stage != PrebuildStage.Fill) EndFill(_filling, filled: false);
            else TickFilling(_filling, deltaSeconds);
            return;
        }
        _sinceFill += deltaSeconds;
        if (gate.Busy) return;
        if (gate.Stage == PrebuildStage.Structure) TryStructureMount();
        else if (gate.Stage == PrebuildStage.Fill)
        {
            SampleSignatures(deltaSeconds);
            if (WindowPrebuildRules.MayStartFill(gate.BehindLoading, gate.InCombat, _sinceFill)) TryStartFill(gate.BehindLoading);
        }
    }

    // Hidden (structure-built, parked or not mounted yet), or Visible-but-not-drawn and mounted (ShouldRender false).
    private bool Wants(Entry e)
        => e.Reg.Spec.Prebuild && !e.Removed && !e.Drawn && (!e.Visible || (e.Token != null && _renderer.IsAlive(e.Token)));

    private void TryStructureMount()
    {
        if (_renderer is not IWindowStructureMount sm) return;
        foreach (var e in _windows.Values)
        {
            if (!e.Reg.Spec.Prebuild || e.Removed || e.Visible || e.StructureFailed || (e.Token != null && _renderer.IsAlive(e.Token))) continue;
            var hitchT = HitchProbe.Begin();
            e.Token = sm.MountStructure(e.Reg);
            if (hitchT != 0L) HitchProbe.End(e.HitchKey(4), hitchT);
            if (e.Token == null) { e.StructureFailed = true; return; }   // canvas gone / build threw: the fill mounts it
            e.Parked = true; e.PrebuildFilled = false;
            e.VisRestoreOwed = true;            // the mount's persisted show/hide restore runs on the first real show
            ApplySavedRect(e, applyVisibility: false);   // position only; never shows a hidden window
            return;                             // one ~50-220 ms build per tick
        }
    }

    private void TryStartFill(bool behindLoading)
    {
        if (_renderer is not IWindowBackgroundWarm warm) return;
        foreach (var e in _windows.Values)
        {
            if (!Wants(e) || e.PrebuildFilled) continue;
            _log.Info($"[Prebuild] {e.Reg.Spec.Id}: fill {(e.Token == null ? "(mount)" : e.Visible ? "(held hidden by ShouldRender)" : "(built, hidden)")} "
                + $"{(behindLoading ? "behind the loading screen" : "in play")}{(e.FillReason != null ? " — " + e.FillReason : "")}");
            e.FillReason = null;
            warm.SetBackgroundWarm(e.Reg.Spec.Id, true);   // before the mount / un-park: it lands off-screen
            e.BgWarming = true; e.StructureFailed = false;
            _filling = e; _fillTicks = 0; _fillSeconds = 0f;
            return;
        }
    }

    private void TickFilling(Entry e, float dt)
    {
        if (!e.BgWarming) { _filling = null; _sinceFill = 0f; return; }   // a real open took it over
        var warm = _renderer as IWindowBackgroundWarm;
        if (!e.Removed && !WindowPrebuildRules.FillEnds(++_fillTicks, _fillSeconds += dt,
                warm?.IsBackgroundWarmDone(e.Reg.Spec.Id) ?? true)) return;
        EndFill(e, filled: true);
    }

    private void EndFill(Entry e, bool filled)
    {
        // Logs "[Prebuild] <id>: background warm done / cut short" (renderer, [Hitch] probe on).
        (_renderer as IWindowBackgroundWarm)?.SetBackgroundWarm(e.Reg.Spec.Id, false);
        e.BgWarming = false;                             // this tick's TickEntry parks it (moved home while inactive)
        e.PrebuildFilled = filled;
        if (filled) e.Watch.Reset(Signature(e));         // what this fill applied
        _filling = null; _sinceFill = 0f;
    }

    // ~2/s: a filled, undrawn window whose content moved gets filled again. Drawn → its bindings keep it current, so the
    // signature just becomes the new baseline (ContentWatch).
    private void SampleSignatures(float dt)
    {
        if ((_sigClock += dt) < WindowPrebuildRules.SignatureEverySeconds) return;
        _sigClock = 0f;
        foreach (var e in _windows.Values)
        {
            if (!e.Reg.Spec.Prebuild || e.Removed || !e.PrebuildFilled || e.Reg.Spec.PrebuildSignature == null) continue;
            var shown = e.Drawn;
            if (!e.Watch.Changed(Signature(e), shown) || shown) continue;
            e.PrebuildFilled = false; e.FillReason = "content changed while hidden";
        }
    }

    // Plugin code: a throwing signature reads as "unchanged", never breaks the tick.
    private static int Signature(Entry e)
    {
        try { return e.Reg.Spec.PrebuildSignature?.Invoke() ?? 0; } catch { return e.Watch.Baseline; }
    }

    // TickEntry: a filling window that is Visible AND would draw now (its ShouldRender passes) takes over — the open wins.
    // Visible but still suppressed keeps filling (it isn't on screen either way).
    private bool FillTakenOver(Entry e)
    {
        if (!e.BgWarming || !e.Visible) return false;
        try { if (WindowGatingPolicy.IsDrawSuppressed(e.Reg.Spec)) return false; } catch { return false; }
        EndFillForRealShow(e);
        return true;
    }

    // Warm off (the next apply moves it home — a real show from the renderer's view, so [UiPerf] winshow from=first) and
    // repaint now. A SetVisible(true) during the fill (ActivateOnShow kept armed meanwhile) puts it on top as if just
    // opened; a ShouldRender flip on an already-Visible window never re-activated before, so it doesn't now either.
    private void EndFillForRealShow(Entry e)
    {
        (_renderer as IWindowBackgroundWarm)?.SetBackgroundWarm(e.Reg.Spec.Id, false);
        e.BgWarming = false; e.PrebuildFilled = true; e.Dirty = true;
        if (e.ActivateOnShow && e.Token != null && _renderer.IsAlive(e.Token)) _order?.Activate(e.Token);
        e.ActivateOnShow = false;
    }

    // TickEntry, a Visible mounted window: spend ActivateOnShow — except mid-fill, so the takeover can still put a
    // SetVisible(true) window on top — and run the persisted show/hide restore a prebuild mount owes. True = that restore
    // hid it: stop this tick (the next one parks it, as a cold mount's restore did).
    private bool SettleShowFlags(Entry e)
    {
        if (e.BgWarming) return false;
        e.ActivateOnShow = false;
        if (!e.VisRestoreOwed || !e.Visible) return false;
        RestoreOwedVisibility(e);
        return !e.Visible;
    }

    // SafeApply: what this apply did (a fill's apply doesn't count). Drawn → current bindings → counts as filled.
    private static void NoteDrawn(Entry e, bool drawn)
    {
        if (e.BgWarming) return;
        e.Drawn = drawn;
        if (drawn && e.Reg.Spec.Prebuild) e.PrebuildFilled = true;
    }

    // TickEntry, first real show of a window a prebuild mounted: the persisted show/hide restore a normal mount does
    // (ApplySavedRect applyVisibility) — skipped at the prebuild mount so it could never SHOW a hidden window. A persisted
    // hide therefore re-hides it exactly as it did on a cold mount. Position is left alone (a plugin may have placed it).
    private void RestoreOwedVisibility(Entry e)
    {
        e.VisRestoreOwed = false;
        if (_storage is null || _resolution is null || e.Reg.Spec.DismissOnOutsideClick) return;
        var (_, visible) = StorageGet(e.Reg.Spec, _resolution(), ResolveAnchoredDefault(e.Reg.Spec), CanvasScale);
        e.SetVisibleRestore(visible);
    }

    private sealed partial class Entry
    {
        public bool BgWarming;        // being filled off-screen: mounted + applied although hidden (TickEntry)
        public bool PrebuildFilled;   // filled since the last login into the world (or shown: bindings current)
        public bool VisRestoreOwed;   // mounted by a prebuild: RestoreOwedVisibility on the first real show
        public bool StructureFailed;  // MountStructure returned null: leave it to the fill
        public string? FillReason;    // for the "[Prebuild] <id>: fill" log line
        public bool Drawn;            // the last apply drew it (on screen); false while parked / suppressed / unmounted
        public readonly ContentWatch Watch = new();
    }
}
