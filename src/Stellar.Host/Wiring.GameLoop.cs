namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // Scene-transition gate. The throttled InvokeRepeating tick runs decoupled from the game loop, so
    // unlike the old Game.Update postfix it can run our service/state reads (player state, the 490-item
    // inventory walk, plugin updates) DURING a scene switch's world-connection handshake — observed to
    // disrupt the switch (server disconnect [50000]/[50011]). While transitioning (from OnLeaveScene until
    // the next OnEnterScene) the tick is skipped entirely. Safety-cleared if a transition never completes.
    // Gate the tick to ONLY run when logged in AND in a stable scene. Starts gated (no work at boot /
    // title / char-select — that title-phase work was observed to corrupt the later world connect even
    // though it ran nowhere near the handshake). Cleared on OnEnterScene only once logged in; re-gated on
    // OnLogin (world-connect handshake), OnLeaveScene (scene switch), and OnLogout.
    private volatile bool _sceneTransitioning = true;
    private volatile bool _loggedIn;

    // Opens the tick gate. Called from OnLogin (start of the world-connection handshake) AND OnLeaveScene
    // (scene switch). The decoupled tick must do NO game-state work until the next OnEnterScene, because
    // running it during the connect/switch handshake disrupts it (server disconnect [50000]). The
    // disruptive work was observed running in the connect phase (after LoginEvent, BEFORE OnLeaveScene),
    // so gating only leave→enter was not enough.
    private void BeginSceneTransition()
    {
        _sceneTransitioning = true;
        _clientState?.SetWorldActive(false);   // game-state units self-gate on IClientState.IsWorldActive
        Log.LogInfo("[Ticker] tick gated off (world connect / scene switch) until next OnEnterScene");
    }

    // Pure boolean — nothing to misfire. Set true on OnLogin/OnLeaveScene, false on OnEnterScene.
    // (If OnEnterScene never fires the tick stays gated until the next login/scene — acceptable: a
    // relog re-fires OnLogin→OnEnterScene. No timer/counter, since both earlier safety mechanisms had bugs.)
    private bool IsTickGatedBySceneTransition() => _sceneTransitioning;

    private void OnEnterScene(object? instance, object?[] args)
    {
        _gameInstance ??= instance;   // capture for the resolver probe (no per-frame Update hook)
        // Clear the gate ONLY once logged in — so game-state work stays silent through boot/title/char-select
        // (where it corrupts the later world connect) and resumes only in-world. This rising edge of
        // IsWorldActive while logged in is also the CharSelect→World phase transition (Phase is CharSelect
        // here — IsLoggedIn/OnLogin already moved it off TitleScreen at char-select). RaisePhase is a no-op
        // if already World, so in-world zone loads don't re-fire it.
        if (_loggedIn && _sceneTransitioning)
        {
            _sceneTransitioning = false;
            _clientState?.SetWorldActive(true);
            _clientState?.RaisePhase(Stellar.Abstractions.Domain.GamePhase.World);
            Log.LogInfo("[Ticker] tick gate cleared (in-world) — resuming framework tick");
        }
        var sceneName = args.Length > 0 ? args[0]?.ToString() : null;
        _clientState!.RaiseSceneChanged(sceneName);
        // Entering a scene is the signal that the live CharDataComponent has
        // (or is about to) become readable — clear the inventory probe's
        // resolution backoff so it re-attempts the pull-based candidates at
        // full speed instead of sitting in the long post-boot backoff.
        _inventoryProbe!.OnLifecycleAdvanced();
        _harmonyBridge!.Publish("Panda.Core.OnEnterSceneEvent", sceneName);
        // Fresh ungated diagnostics allowance for whichever dungeon/raid this scene turns out to
        // be — see PandaEntityStateProbe.Diagnostics.cs for why a session-lifetime budget was
        // wrong (2026-07-28 review fix). Null until hot-update-ready wiring constructs it.
        _entityStateProbe?.ResetObservationBudget();

        // Phase 9a Task 17 — auto-recon for the native UI allowlist. Gated by
        // STELLAR_NATIVEUI_RECON=1; one-shot per process. Fires on the first
        // in-world scene.
        if (sceneName == "7" || sceneName == "8")
        {
            var log = new Stellar.Infrastructure.BepInExAdapters.BepInExPluginLog(Log);
            TryAutoReconNativeUi(log);
        }
    }
}
