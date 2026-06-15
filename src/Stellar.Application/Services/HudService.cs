using System;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Runtime for the uGUI HUD toolkit. Mounts HUDs when their anchor is present, re-applies
/// values at a capped ~10 Hz (or immediately on MarkDirty), and self-heals on scene-change destroys.
/// Position persistence + drag live in the sibling partial (Task 5).</summary>
internal sealed partial class HudService : IHudHost
{
    private const float ApplyInterval = 0.1f;   // ~10 Hz value refresh; the animator smooths bars per-frame
    private readonly IHudRenderer _renderer;
    private readonly IPluginLog _log;
    private readonly IGameMenuState _menuState;
    private readonly IClientState _clientState;
    private readonly Dictionary<string, Entry> _huds = new();
    private float _accum;

    public HudService(IHudRenderer renderer, IPluginLog log, IGameMenuState menuState, IClientState clientState)
    { _renderer = renderer; _log = log; _menuState = menuState; _clientState = clientState; }

    public IHudHandle Register(HudSpec spec)
    {
        if (_huds.ContainsKey(spec.Id)) { _log.Error($"[Hud] duplicate id '{spec.Id}'; ignored."); return new NoOpHandle(); }
        var e = new Entry(spec); _huds[spec.Id] = e; return e;
    }

    /// <summary>Driven from the host per-tick path. <paramref name="deltaSeconds"/> drives the apply cap.</summary>
    public void Tick(float deltaSeconds)
    {
        // Drive bar smoothing here (on the throttled framework ticker) instead of HudBarAnimator
        // having its own Unity Update — the HUD canvas is always-on, so a per-frame MonoBehaviour
        // Update would cost the ~12-18 fps managed-crossing tax even in HUD-only steady state.
        _renderer.TickAnimations(deltaSeconds);
        _accum += deltaSeconds;
        var applyNow = _accum >= ApplyInterval;
        // Reset to 0 (not -= ApplyInterval) on purpose: a large delta spike (hitch/alt-tab)
        // collapses to a single apply rather than a catch-up burst of back-to-back applies.
        if (applyNow) _accum = 0f;
        foreach (var e in _huds.Values) if (!e.Removed) TickEntry(e, applyNow);
        PruneRemoved();
    }

    private void TickEntry(Entry e, bool applyNow)
    {
        if (!e.Visible) { DestroyIfMounted(e); return; }
        // Self-heal probe is intentionally uncapped (runs every tick, not gated by applyNow) so a
        // scene-change destroy is detected and remounted promptly; if HUD counts ever grow, rate-limit here.
        if (e.Token is null || !_renderer.IsAlive(e.Token))
        {
            if (!_renderer.IsAnchorAvailable(e.Spec.Anchor)) { e.Token = null; return; }
            e.Token = SafeMount(e);
            if (e.Token is null) return;
            ApplySavedRect(e);          // Task 5 partial; restore persisted position
            SafeApply(e);               // first paint
            e.Dirty = false;
            e.LastShownRect = _renderer.GetRect(e.Token);   // cache for the editor's hidden-rect fallback
            return;
        }
        if (applyNow || e.Dirty) { SafeApply(e); e.Dirty = false; }
        if (applyNow && e.Token != null) e.LastShownRect = _renderer.GetRect(e.Token);
    }

    private object? SafeMount(Entry e)
    { try { return _renderer.Mount(e.Spec); } catch (Exception ex) { _log.Warning($"[Hud/{e.Spec.Id}] mount: {ex.Message}"); return null; } }
    private void SafeApply(Entry e)
    {
        // Auto-hide gate: gameplay HUDs (cooldowns, meter, stats) should not draw over a full-screen game menu.
        // The renderer deactivates the root + skips the value pull when hidden (perf win). Recomputed each apply
        // (~10 Hz) → responds within ≤100 ms of a menu opening/closing.
        var hide = WindowGatingPolicy.IsDrawSuppressed(e.Spec.AutoHideBehindGameMenus, e.Spec.HideUntilInWorld,
            _menuState.IsFullScreenMenuOpen, _clientState.IsLoggedIn);
        var id = "hud:" + e.Spec.Id;
        PerfProbe.BeginWindow(id);
        try { _renderer.ApplyValues(e.Token, e.Spec, hide); }
        catch (Exception ex) { _log.Warning($"[Hud/{e.Spec.Id}] apply: {ex.Message}"); }
        finally { PerfProbe.EndWindow(id); }
    }
    private void DestroyIfMounted(Entry e) { if (e.Token is null) return; _renderer.Destroy(e.Token); e.Token = null; }

    private void PruneRemoved()
    {
        List<string>? gone = null;
        foreach (var e in _huds.Values) if (e.Removed) { DestroyIfMounted(e); (gone ??= new()).Add(e.Spec.Id); }
        if (gone != null) foreach (var id in gone) _huds.Remove(id);
    }

    public void DisposeAll() { foreach (var e in _huds.Values) DestroyIfMounted(e); _huds.Clear(); }

    private sealed class Entry : IHudHandle
    {
        public Entry(HudSpec spec) { Spec = spec; }
        public HudSpec Spec { get; }
        public object? Token; public bool Visible = true; public bool Dirty = true; public bool Removed;
        public WindowRect LastShownRect;   // last rect while mounted — the editor's re-enable outline uses it when hidden
        public bool IsShown => Token != null && Visible;
        public void MarkDirty() => Dirty = true;
        public void SetVisible(bool visible) => Visible = visible;
        public void Remove() => Removed = true;
    }
    private sealed class NoOpHandle : IHudHandle
    { public bool IsShown => false; public void MarkDirty(){} public void SetVisible(bool v){} public void Remove(){} }
}
