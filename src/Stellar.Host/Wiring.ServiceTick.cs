using System;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private bool _perfFlagsLogged;
    // Frame-rate uncap delegate — diff-state and Unity writes live in Infrastructure.FrameRateReconciler
    // behind IFrameRateLimiter; injected by Host after all services are constructed.
    private Stellar.Application.Abstractions.IFrameRateLimiter? _frameLimiter;
    private Stellar.Infrastructure.Unity.UnityTickHost? _tickHost;
    private Stellar.Application.Services.TickScheduler? _scheduler;
    private readonly Stellar.Application.Services.RateGate _globalGate = new();
    // Last resolution observed by the global tick, for the layout re-clamp on a resolution change. Default
    // (0×0) until the first observation, so the very first beat only records the baseline (no spurious reclamp).
    private Stellar.Abstractions.Domain.Resolution _lastLayoutRes;
    // scaleFactor observed on the previous beat. We reapply on a CHANGE to it (see
    // ReclampLayoutOnResolutionChange) — the CanvasScaler settles a beat or two AFTER a resolution change, so
    // its change is what tells us the correct factor is finally live. -1 = never observed (skip first beat).
    private float _lastLayoutScale = -1f;
    // Last window-canvas generation seen. A bump = the canvas was destroyed+rebuilt (scene change), so every window
    // remounted and parked its layout (the fresh CanvasScaler still reported the default 1.0). We latch a ONE-shot
    // corrective reapply, deferred until the scaler settles, so it clamps against the real bound. -1 = never observed.
    private int _lastCanvasGen = -1;
    private bool _pendingCanvasReapply;

    // Reconciles the live runtime to the Performance settings (PerfControls), which are driven by the
    // Settings → Performance panel and seeded from config at boot. Runs every tick BEFORE the scene
    // gate so a setting change applies even between scenes; each reconcile is a cheap no-op when the
    // live state already matches. Replaces the old one-shot env-only uncap with a self-healing toggle.
    private void MaybeApplyPerfExperiment()
    {
        if (!_perfFlagsLogged)
        {
            _perfFlagsLogged = true;
            Log.LogInfo($"[Perf] flags: Uncap={Stellar.Abstractions.Diagnostics.PerfControls.Uncap} " +
                        $"rate={Stellar.Abstractions.Diagnostics.PerfControls.UpdateRateHz}Hz " +
                        $"cwd={System.IO.Directory.GetCurrentDirectory()}");
        }

        // Authoritative + order-safe: each tick reconciles the live ticker to the scheduler's master rate
        // (no-op when unchanged). Covers boot ordering too.
        _tickHost?.Reschedule(_scheduler?.MasterRateHz ?? Stellar.Abstractions.Diagnostics.PerfControls.UpdateRateHz);

        // Frame-rate uncap — RE-ENFORCED every tick while ON so any game-side cap re-application
        // (graphics-settings change / scene load / login) is immediately overridden. Diff-state +
        // Unity writes live in Infrastructure.FrameRateReconciler behind IFrameRateLimiter (B-01).
        _frameLimiter?.Reconcile();
    }

    // Driven by StellarTicker's InvokeRepeating schedule at _scheduler.MasterRateHz — NOT a
    // per-frame Game.Update postfix — so most rendered frames have ZERO managed entry (the
    // ~12-18 fps managed-crossing tax). masterDt is real seconds since the previous tick (≈ 1/masterRate).
    // Three-band structure:
    //   Band 1 — every master beat: exchange probe drain only (latency-critical; cheap when idle).
    //   Band 2 — per-plugin Updates at each plugin's own rate (_scheduler.Beat).
    //   Band 3 — global-gated expensive work (draw/refresh/input) at PerfControls.UpdateRateHz;
    //            equip + loadout drains also run here (no latency need; avoids 8× Lua cost at ramp rate).
    private void RunFrameworkTick(float masterDt)
    {
        MaybeApplyPerfExperiment();

        // The tick is a DUMB DISPATCHER — it runs every phase and gates nothing (so window draw, input, and
        // hotkeys work at the title screen). Correctness moved to a one-line `if (!IsWorldActive) return;` at
        // the top of each game-state unit (services self-gate; the Host plumbing below self-gates too). See
        // docs/game-phases-design.md §5.2 / §9.

        // Time the whole per-tick Update path (plugin Updates + service refreshes). No-op unless PERFHUD.
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginUpdate();
        try
        {
            // Band 1 — every master beat (exchange only; cheap when idle — empty-queue dequeue + empty active-list loop).
            Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("fw:exchange");
            DrainExchangeProbe();
            Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("fw:exchange");

            // Band 2 — per-plugin Updates, each plugin firing at its own registered rate.
            Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("fw:plugins");
            _scheduler?.Beat(masterDt);
            Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("fw:plugins");

            // Band 3 — expensive draw/refresh/input work, gated to the global rate.
            if (_globalGate.Crossed(masterDt, Stellar.Abstractions.Diagnostics.PerfControls.UpdateRateHz))
                RunGlobalRateWork(_globalGate.LastDt);
        }
        finally
        {
            Stellar.Abstractions.Diagnostics.PerfProbe.EndUpdate();
        }

        // Commit timings (no-op unless PERFHUD). masterDt is the master tick interval; [Perf] avgFps
        // reflects the master tick rate, not the render frame rate — read real FPS from DXVK when throttled.
        Stellar.Abstractions.Diagnostics.PerfProbe.RecordFrame(masterDt);
    }

    // Extracted so RunFrameworkTick stays under the 50-LoC analyzer limit (STELLAR0002).
    // Runs only on the global-gated beat (PerfControls.UpdateRateHz); globalDt is _globalGate.LastDt.
    private void RunGlobalRateWork(float globalDt)
    {
        Stellar.Abstractions.Diagnostics.PerfProbe.MarkDrawFrame();
        _framework!.SetScreen(UnityEngine.Screen.width, UnityEngine.Screen.height);
        _framework!.SetCanvasScale(_windowService?.CanvasScale ?? 1f);   // canvas-unit dims for IFramework.CanvasWidth/Height
        ReclampLayoutOnResolutionChange();   // pull windows/HUD back on-screen when the resolution changes
        // Login-view detection — UN-gated (runs in every phase, incl. Startup where IsWorldActive is false, so it
        // MUST NOT sit behind the IsWorldActive gate below). A pure UI active-state read, safe every phase like the
        // draw services. Latches Startup→TitleScreen once login_main is up; the one-way guard lives in the service.
        _loginViewProbe?.Tick();
        if (_loginViewProbe?.IsLoginViewActive == true) _clientState!.NotifyLoginViewActive();
        // Loading-screen detection — ALSO un-gated, for the same reason: the loading screen is up precisely
        // while IsWorldActive is false (the zone-load handshake), so the gated menu-state probe below is frozen
        // and can't own the Loading bit. This pure active-state read is the SOLE owner of GameUIState.Loading,
        // set every phase; the gated menu-state probe no longer touches that bit (SetUiState strips it).
        _loadingScreenProbe?.Tick();
        _clientState!.SetLoadingActive(_loadingScreenProbe?.IsLoadingScreenActive ?? false);
        // uGUI native-canvas injection — UN-gated so title-screen anchors (LoginSidebar) inject too. It reads
        // GameObject active-state + builds uGUI buttons (no game-state/network touch), safe every phase like the
        // probes above. In-world anchors (MainMenuRail/HudTopRight) simply won't resolve until their parents exist.
        _uguiInjection?.Tick(globalDt);
        _uguiAdapter?.TickGlow();   // un-gated too, so the login-sidebar glow star animates at the title screen
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("fw:internal");
        // Game-state Host plumbing: _framework.Tick fires host-internal Update subscribers (native-UI
        // injection, menu-state probe, …) that touch the live game — self-gate on IsWorldActive.
        if (_clientState!.IsWorldActive) _framework!.Tick(globalDt);   // (plugins use _scheduler, not this)
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("fw:internal");
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("fw:gamedata");
        TryLoadGameDataEagerOnce();        // fires once when Bokura.*TableBase handles are populated
        DrainGameDataDeferred();           // one deferred table per tick; no-op until eager done / queue empty
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("fw:gamedata");
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("fw:equiploadout");
        DrainEquipAndLoadout();            // equip + loadout probes — no latency need; kept at global rate
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("fw:equiploadout");
        RefreshPerTickServices(globalDt);
        ProbeGameRootOnce(_gameInstance);
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("svc:worldattr");
        _worldAttrProbe?.Tick();   // main-thread read of ZWorld AttrDeathCount(348) → Defeated (no-op in town)
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("svc:worldattr");
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("fw:input");
        TickInputAndHotkeys();
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("fw:input");
        // Layout edit-mode input (select/drag) — driven from the tick AFTER the input poll (so the latched
        // mouse edge + pointer are fresh). Edit-mode interaction is fully decoupled from any IMGUI/OnGUI
        // handler; all rendering goes through the uGUI path (HudThemeAssets / WindowThemeAssets bake on demand).
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("fw:layout");
        _layoutOverlay?.TickInput();
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("fw:layout");
    }

    // Nothing re-clamps layout on a resolution change — placement/clamp happens only on mount — so a window
    // or HUD near the bottom/right edge falls off-screen when the user drops to a smaller resolution. Detect
    // the change here (two int compares, every global beat) and re-clamp every mounted element IN PLACE: keep
    // its current position, just pull it back on-screen if now off. Idempotent when nothing is off-screen.
    // NB: this is a clamp, NOT a slot reload — the user's arrangement is preserved, only tucked back in bounds.
    private void ReclampLayoutOnResolutionChange()
    {
        var curRes = _inputGateway?.CurrentResolution ?? default;
        var curScale = _windowService?.CanvasScale ?? 1f;
        var curGen = _windowService?.CanvasGeneration ?? 0;
        var scaleReady = _windowService?.CanvasScaleReady ?? true;
        var resChanged = curRes.Width != _lastLayoutRes.Width || curRes.Height != _lastLayoutRes.Height;
        var scaleChanged = _lastLayoutScale >= 0f && System.Math.Abs(curScale - _lastLayoutScale) > 0.001f;

        // Canvas recreate (scene change destroyed + WindowRenderer rebuilt the canvas): latch a one-shot corrective
        // reapply, but hold it until the CanvasScaler settles — reapplying against the transient default 1.0 would
        // re-introduce the very snap bug. Belt to WindowService.Layout's per-window defer (suspenders).
        if (_lastCanvasGen >= 0 && curGen != _lastCanvasGen) _pendingCanvasReapply = true;
        _lastCanvasGen = curGen;
        var genReapply = _pendingCanvasReapply && scaleReady;
        if (!resChanged && !scaleChanged && !genReapply) return;

        var firstObservation = _lastLayoutRes.Width == 0;
        _lastLayoutRes = curRes;
        _lastLayoutScale = curScale;
        if (firstObservation || curRes.Width <= 0) return;   // never reapply on the first-ever observation

        // Re-apply on resolution OR scaleFactor change OR a settled canvas recreate. The scale/recreate paths are the
        // key fix: the CanvasScaler settles scaleFactor a beat or two LATER, and Get's canvas-unit clamp needs the
        // CORRECT factor — so we reapply only once the right value is live. ReapplyLayout self-gates on ready too.
        if (genReapply) _pendingCanvasReapply = false;   // consume the one-shot
        _windowService?.ReapplyLayout();          // position-only after Part 1
        _hudService?.ReapplyLayout();             // position-only after Part 2
        _nativeUi?.ReapplyForActiveSlot(curRes, applyVisibility: false);   // reposition, never toggle show/hide
    }

    // Band 1 — drained EVERY master beat so a ramped plugin's exchange RPC round-trips complete
    // proportionally faster. Cheap when idle (empty-queue dequeue + empty active-list loop).
    private void DrainExchangeProbe()
    {
        try { _exchangeProbe!.DrainPendingDispatches(); }
        catch (Exception ex) { Log.LogWarning($"[boot] exchange drain threw: {ex.Message}"); }
    }

    // Per-frame input + hotkey poll, driven from the framework tick (Phase E: there is no
    // OnGUI handler anymore). Unity runs Update() once per frame, so no per-OnGUI-pass gate is
    // needed. Run AFTER _framework.Tick so hotkey evaluation sees the same frame's input.
    private void TickInputAndHotkeys()
    {
        _inputGateway?.TickPoll();
        _hotkeyService?.Tick();
        _noticeTipService?.Tick();
    }

    private void RefreshPerTickServices(float deltaTime)
    {
        // Drain any pending FileSystemWatcher events to the game thread BEFORE
        // service refreshes — SectionChanged listeners (Subscribe/Unsubscribe
        // reconciliation in plugins) must run before downstream services
        // observe new state on this tick.
        _configStore?.DrainExternalEvents();

        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("svc:pstate");
        try { _playerState!.Refresh(_playerStateProbe!); }
        catch (Exception ex) { Log.LogWarning($"[boot] player state refresh threw: {ex.Message}"); }
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("svc:pstate");

        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("svc:pstats");
        try { _playerStatsService!.Refresh(_playerStatsProbe!); }
        catch (Exception ex) { Log.LogWarning($"[boot] player stats refresh threw: {ex.Message}"); }
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("svc:pstats");

        // Phase 7: poll module inventory at 1 Hz. Time-based (deltaTime accumulator) rather than a
        // tick count, so the rate is independent of PerfControls.UpdateRateHz (a tick-count threshold
        // would drift with the Update Rate slider). The capture hook is installed at boot
        // (WorldNtfStub.OnCallStub), so the resolver serves ONLY the latched-capture reader — no broad
        // AppDomain scan. The refresh is therefore cheap whether or not a sync has landed.
        _inventoryAccumSeconds += deltaTime;
        if (_inventoryAccumSeconds >= 1.0)
        {
            _inventoryAccumSeconds = 0.0;
            try { _inventoryService!.Refresh(); }
            catch (Exception ex) { Log.LogWarning($"[boot] inventory refresh threw: {ex.Message}"); }
            try { _resonanceService!.Refresh(); }
            catch (Exception ex) { Log.LogWarning($"[boot] resonance refresh threw: {ex.Message}"); }
        }

        // DrainEquipAndLoadout() is called from Band 3 (RunGlobalRateWork), not here.

        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("svc:chat");
        _chatService!.Drain();
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("svc:chat");

        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("svc:combat");
        _combatService!.Drain();
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("svc:combat");

        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("svc:party");
        _partyService!.Drain();
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("svc:party");

        TickOverlayServices(deltaTime);
    }

    // Band 3 — global-rate cadence (these probes have no latency-sensitive consumer; keeping them at the
    // global rate avoids 8× Lua-read / allocation cost during a rate ramp). Both probes touch the
    // game's main-thread-only Lua VM, so this runs on the Update tick.
    [Stellar.Abstractions.Diagnostics.WorldGated]
    private void DrainEquipAndLoadout()
    {
        if (!_clientState!.IsWorldActive) return;   // equip/loadout probes touch the game's main-thread Lua VM
        try { _moduleEquipProbe!.DrainPendingCompletions(); }
        catch (Exception ex) { Log.LogWarning($"[boot] equip drain threw: {ex.Message}"); }

        try { _loadoutProbe!.TryResolveBridgeIfDue(); _loadoutProbe!.DrainPendingCompletions(); _loadoutService!.Tick(); }
        catch (Exception ex) { Log.LogWarning($"[boot] loadout tick threw: {ex.Message}"); }
    }

    // uGUI HUD + window toolkits + the SP1 keyboard gate, ticked from the throttled tick. deltaTime is the
    // real seconds since the previous tick (≈1/UpdateRateHz) — pass it (NOT Time.deltaTime, the render-frame
    // delta) so HUD bar animation converges at the right speed at any tick rate. The gate suppresses the game
    // keyboard while a window text field is focused (stops the wasd leak); guarded to defer to the spike.
    private void TickOverlayServices(float deltaTime)
    {
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("svc:toast");
        TickNotifications(deltaTime);   // animate the toast stack on the framework tick delta
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("svc:toast");
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("svc:hud");
        _hudService?.Tick(deltaTime);
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("svc:hud");
        if (Stellar.Abstractions.Diagnostics.PerfProbe.IsEnabled) _perfOverlay?.RefreshTopWindows();
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("svc:window");
        _windowService?.Tick(deltaTime);
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("svc:window");
        if (_keyboardGate != null)
            _keyboardGate.SetSuppressed(_windowService?.AnyFieldFocused ?? false);
        _hotkeysCapturePoll?.Invoke();   // uGUI Hotkeys panel key capture (no-op unless a cell is capturing)
        _themeEditorPoll?.Invoke();      // uGUI Themes colour-editor drag-release flush (no-op unless editing)
    }
}
