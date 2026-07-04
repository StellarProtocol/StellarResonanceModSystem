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

        // Don't touch game state during a scene switch / world-connection handshake (disrupts the switch).
        if (IsTickGatedBySceneTransition()) return;

        // Time the whole per-tick Update path (plugin Updates + service refreshes). No-op unless PERFHUD.
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginUpdate();
        try
        {
            // Band 1 — every master beat (exchange only; cheap when idle — empty-queue dequeue + empty active-list loop).
            DrainExchangeProbe();

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
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("fw:internal");
        _framework!.Tick(globalDt);       // fires host-internal Update subscribers (plugins use _scheduler)
        Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("fw:internal");
        TryLoadGameDataEagerOnce();        // fires once when Bokura.*TableBase handles are populated
        DrainGameDataDeferred();           // one deferred table per tick; no-op until eager done / queue empty
        DrainEquipAndLoadout();            // equip + loadout probes — no latency need; kept at global rate
        DrainDungeonDeferred();            // dungeon lua-path deliveries deferred for scene-teardown crash safety
        RefreshPerTickServices(globalDt);
        ProbeGameRootOnce(_gameInstance);
        TrySubscribeDungeonSync();         // bounded retry until the game's MessagePipe is reachable
        TickInputAndHotkeys();
        // Layout edit-mode input (select/drag) — driven from the tick AFTER the input poll (so the latched
        // mouse edge + pointer are fresh). Edit-mode interaction is fully decoupled from any IMGUI/OnGUI
        // handler; all rendering goes through the uGUI path (HudThemeAssets / WindowThemeAssets bake on demand).
        _layoutOverlay?.TickInput();
    }

    // Band 1 — drained EVERY master beat so a ramped plugin's exchange RPC round-trips complete
    // proportionally faster. Cheap when idle (empty-queue dequeue + empty active-list loop).
    private void DrainExchangeProbe()
    {
        try { _exchangeProbe!.DrainPendingDispatches(); }
        catch (Exception ex) { Log.LogWarning($"[boot] exchange drain threw: {ex.Message}"); }
    }

    // Dungeon lua-path deliveries (SyncDungeonData 23 + NotifyStartPlayingDungeon 55) are
    // NEVER processed inside ZLuaStub.OnCallStub — inline processing there crashed the client
    // during a post-dungeon scene load. The probe queues them; this drain runs on the gated
    // tick, so queued items wait out any scene transition automatically.
    private void DrainDungeonDeferred()
    {
        try { _dungeonProbe?.DrainDeferred(); }
        catch (Exception ex) { Log.LogWarning($"[boot] dungeon deferred drain threw: {ex.Message}"); }
    }

    // Dungeon dirty-delta MessagePipe subscription — no-op once subscribed (or once the
    // bounded attempt budget is spent). Runs on the gated tick because the subscriber is
    // resolved from the game's VContainer / GlobalMessagePipe, which come up after boot.
    private void TrySubscribeDungeonSync()
    {
        if (_messagePipeBridge is null) return;
        _dungeonSyncSubscription?.TrySubscribe(_messagePipeBridge);
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
    private void DrainEquipAndLoadout()
    {
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
        TickNotifications(deltaTime);   // animate the toast stack on the framework tick delta
        _hudService?.Tick(deltaTime);
        if (Stellar.Abstractions.Diagnostics.PerfProbe.IsEnabled) _perfOverlay?.RefreshTopWindows();
        _windowService?.Tick(deltaTime);
        if (_keyboardGate != null)
            _keyboardGate.SetSuppressed(_windowService?.AnyFieldFocused ?? false);
        _hotkeysCapturePoll?.Invoke();   // uGUI Hotkeys panel key capture (no-op unless a cell is capturing)
        _themeEditorPoll?.Invoke();      // uGUI Themes colour-editor drag-release flush (no-op unless editing)
    }
}
