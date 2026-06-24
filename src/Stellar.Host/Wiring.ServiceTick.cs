using System;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private bool _perfFlagsLogged;
    // Frame-rate uncap delegate — diff-state and Unity writes live in Infrastructure.FrameRateReconciler
    // behind IFrameRateLimiter; injected by Host after all services are constructed.
    private Stellar.Application.Abstractions.IFrameRateLimiter? _frameLimiter;
    private Stellar.Infrastructure.Unity.UnityTickHost? _tickHost;

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

        // Re-rate the live ticker if the Update Rate setting changed (Reschedule no-ops when unchanged).
        _tickHost?.Reschedule();

        // Frame-rate uncap — RE-ENFORCED every tick while ON so any game-side cap re-application
        // (graphics-settings change / scene load / login) is immediately overridden. Diff-state +
        // Unity writes live in Infrastructure.FrameRateReconciler behind IFrameRateLimiter (B-01).
        _frameLimiter?.Reconcile();
    }

    // Driven by StellarTicker's InvokeRepeating schedule at PerfControls.UpdateRateHz — NOT a
    // per-frame Game.Update postfix — so most rendered frames have ZERO managed entry (the
    // ~12-18 fps managed-crossing tax). deltaTime is real seconds since the previous tick (≈ 1/rate).
    private void RunFrameworkTick(float deltaTime)
    {
        MaybeApplyPerfExperiment();

        // Don't touch game state during a scene switch / world-connection handshake (disrupts the switch).
        if (IsTickGatedBySceneTransition()) return;

        // Time the whole per-tick Update path (plugin Updates + service refreshes). No-op unless PERFHUD.
        Stellar.Abstractions.Diagnostics.PerfProbe.BeginUpdate();
        try
        {
            _framework!.SetScreen(UnityEngine.Screen.width, UnityEngine.Screen.height);
            Stellar.Abstractions.Diagnostics.PerfProbe.BeginSeg("fw:plugins");
            _framework!.Tick(deltaTime);
            Stellar.Abstractions.Diagnostics.PerfProbe.EndSeg("fw:plugins");
            TryLoadGameDataEagerOnce();   // fires once when Bokura.*TableBase handles are populated
            DrainGameDataDeferred();      // one deferred table per tick; no-op until eager done / queue empty
            RefreshPerTickServices(deltaTime);
            ProbeGameRootOnce(_gameInstance);
        }
        finally
        {
            Stellar.Abstractions.Diagnostics.PerfProbe.EndUpdate();
        }

        TickInputAndHotkeys();

        // Layout edit-mode input (select/drag) — driven from the tick AFTER the input poll (so the latched
        // mouse edge + pointer are fresh). Edit-mode interaction is fully decoupled from any IMGUI/OnGUI
        // handler; all rendering goes through the uGUI path (HudThemeAssets / WindowThemeAssets bake on demand).
        _layoutOverlay?.TickInput();

        // Commit timings (no-op unless PERFHUD). deltaTime is the tick interval, so [Perf] avgFps is the
        // TICK rate, not the render frame rate — read real FPS from DXVK when throttled.
        Stellar.Abstractions.Diagnostics.PerfProbe.RecordFrame(deltaTime);
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

        DrainEquipAndLoadout();

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

    // Drains the deferred Lua dispatches + polls completion for the module-equip and
    // loadout (profession-project) probes, and ticks the loadout service's
    // change-detection. Both probes touch the game's main-thread-only Lua VM, so this
    // runs on the Update tick. Extracted from RefreshPerTickServices for the 50-LoC gate.
    private void DrainEquipAndLoadout()
    {
        try { _moduleEquipProbe!.DrainPendingCompletions(); }
        catch (Exception ex) { Log.LogWarning($"[boot] equip drain threw: {ex.Message}"); }

        try { _loadoutProbe!.TryResolveBridgeIfDue(); _loadoutProbe!.DrainPendingCompletions(); _loadoutService!.Tick(); }
        catch (Exception ex) { Log.LogWarning($"[boot] loadout tick threw: {ex.Message}"); }

        try { _exchangeProbe!.DrainPendingDispatches(); }
        catch (Exception ex) { Log.LogWarning($"[boot] exchange drain threw: {ex.Message}"); }
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
