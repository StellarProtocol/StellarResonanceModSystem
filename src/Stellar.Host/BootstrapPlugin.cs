using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Configuration;
using Stellar.Infrastructure.Events;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Game.Protobuf;
using Stellar.Infrastructure.Hooks;
using Stellar.Infrastructure.UI;
using Stellar.Infrastructure.Unity;

namespace Stellar.Host;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed partial class BootstrapPlugin : BasePlugin
{
    public const string PluginGuid = "stellar.framework";
    public const string PluginName = "Stellar Framework";
    // Single source of truth lives in Stellar.Abstractions.Domain.FrameworkVersion —
    // forwarded here as a const so BepInEx's [BepInPlugin] attribute can read it
    // at compile time (attribute args must be constant expressions).
    public const string PluginVersion = Stellar.Abstractions.Domain.FrameworkVersion.Value;

    private const string GameTypeFullName = "Panda.Core.Game";
    private const string UserPluginSubdirectory = "stellar/plugins";

    private static readonly string[] ExpectedHotUpdateAssemblies =
    {
        "Panda.Script", "Panda.Hud", "Panda.Table", "Panda.Streaming",
        "Panda.ZRpcGen", "Panda.ZShowData", "Panda.ZCurve", "Panda.ZPathWay",
    };

    // NOTE: "Update" is deliberately NOT hooked. A per-frame Harmony postfix on Game.Update would
    // cross into the managed runtime every frame — the ~12-18 fps tax. The framework tick runs on
    // the throttled StellarTicker (InvokeRepeating @ UpdateRateHz) instead; the Game instance the
    // resolver probe needs is captured from Init/OnEnterScene (not per-frame).
    private static readonly string[] GameLifecycleMethods =
    {
        "Init", "OnLogin", "OnLogout", "OnEnterScene", "OnLeaveScene",
    };

    // ── Core services (Wiring.Core.cs) ──────────────────────────────────────
    private FrameworkService? _framework;
    private ClientStateService? _clientState;
    private GameEventsService? _gameEvents;
    private PlayerStateService? _playerState;
    private GameDataService? _gameDataService;
    private PlayerStatsService? _playerStatsService;
    private PluginConfigService? _pluginConfigService;
    private FileConfigStore? _configStore;
    private PandaPlayerStateProbe? _playerStateProbe;
    private PandaPlayerStatsProbe? _playerStatsProbe;
    private ChatService? _chatService;
    private CombatService? _combatService;
    private SocialDataCache? _socialDataCache;
    // Generic profile-card action registry: write side → IPluginServices.ProfileCardActions (plugins
    // register buttons); read side (IProfileCardActionSource) → the native-card injector.
    private ProfileCardActionRegistry? _profileCardActions;
    private PartyService? _partyService;
    private HarmonyEventBridge? _harmonyBridge;
    private MessagePipeContainerBridge? _messagePipeBridge;

    // ── Inventory services (Wiring.Inventory.cs) ────────────────────────────
    private PandaInventoryProbe? _inventoryProbe;
    private InventoryService? _inventoryService;
    private PandaModuleEquipProbe? _moduleEquipProbe;
    private ModuleEquipService? _moduleEquipService;
    private PandaTeamControlProbe? _teamControlProbe;
    private PartyControlService? _partyControlService;
    private ResonanceService? _resonanceService;   // self equipped Battle Imagines (CharSerialize.resonance)
    private double _inventoryAccumSeconds;   // time-based 1 Hz inventory poll (rate-independent)

    // ── GameData services (Wiring.GameData.cs / Wiring.GameData.Tick.cs) ───
    private PandaGameDataProbe? _gameDataProbe;
    private GameDataResonance? _gameDataResonance;  // Battle Imagine (Resonance Skill) lookup
    private PandaMLStringResolver? _mlStrings;       // shared MLString resolver (probe + resonance)
    private PandaClientLanguage? _clientLanguage;    // cached client UI language (locale-gates NameDesign fallback)
    private BepInExPluginLog? _gameDataLog;       // captured so the deferred eager-load can log via the same sink
    private bool _gameDataEagerLoaded;
    private bool _gameDataAllLoaded;             // one-shot guard for "all tables loaded" log

    // ── Wire / stub probes (Wiring.Wire.cs) ─────────────────────────────────
    private PandaWireTap? _wireTap;
    private PandaChatProbe? _chatProbe;
    private PandaCombatStubProbe? _combatStubProbe;
    private PandaPartyStubProbe? _partyStubProbe;
    private PandaSocialDataProbe? _socialDataProbe;
    private PandaReadyCheckProbe? _readyCheckProbe;
    private WorldNtfStubDispatcher? _worldNtfDispatcher;
    private WorldNtfLuaStubDispatcher? _worldNtfLuaDispatcher;
    private GrpcTeamNtfStubDispatcher? _grpcTeamNtfDispatcher;
    // Injects the registered profile-card action buttons (IProfileCardActionSource) into the game's
    // native profile card. Ticked from the framework Update; lazy Lua-bridge resolve for the charId read.
    private PandaProfileCardActionInjector? _profileCardActionInjector;

    // ── Plugin host (Wiring.PluginHost.cs) ──────────────────────────────────
    private PluginHost? _pluginHost;
    private Stellar.Application.Services.NoticeTipService? _noticeTipService;

    // ── Resolver probe + UI (Wiring.Resolver.cs / Wiring.PluginRegistry.cs) ─
    private bool _gameRootProbed;
    private Stellar.Infrastructure.Game.ReflectionGameTypeRegistry? _gameTypeRegistry;
    private KeyboardInputGate? _keyboardGate;
    private UGuiInjectionService? _uguiInjection;

    // ── Perf overlay (constructed in SetupPerfOverlay) ──────────────────────
    private PerfOverlayWindow? _perfOverlay;
    private Stellar.Abstractions.Services.IWindowControl? _perfOverlayControl;

    // ── Menu state probe (Wiring.InputLayout.cs) ────────────────────────────
    private Stellar.Infrastructure.Game.PandaMenuStateProbe? _menuState;

    // ─────────────────────────────────────────────────────────────────────────
    // Load / Unload / OnHotUpdateReady — thin orchestrator.
    //
    // Wiring call order in Load():
    //   1. BuildCoreServices         — FrameworkService, ChatService, CombatService, etc.
    //   2. BuildConfigServices       — FileConfigStore, PluginConfigFactory
    //   3. BuildThemeAndColorStack   — NamedThemeService (B-04 first), ThemeRenderer
    //   4. BuildInputAndLayoutServices — UnityInputGateway, HotkeyService, LayoutStorage
    //   5. BuildNativeUiServices     — PerfPrefs, NativeUiService
    //   6. BuildHudServices          — HudRenderer, HudService
    //   7. BuildWindowServices       — WindowRenderer, WindowService
    //   8. BuildLauncherServices     — LauncherRegistry
    //   9. BuildInventoryServices    — PandaInventoryProbe, ModuleEquipProbe
    //  10. WireGameEventsAndPluginHost → BuildUGuiAdapters → ConstructPluginServices → WireFrameworkUpdateEvents
    //
    // Wiring call order in OnHotUpdateReady():
    //   1. SetupPerfOverlay
    //   2. WirePhase9Ui (RegisterSettingsHub, RegisterLauncher, DeclareSettingsHotkey, AttachOverlayLayout)
    //   3. _keyboardGate construction
    //   4. ApplyLifecyclePatches (InstallWireAndStubProbes, HookGameLifecycleMethods)
    //   5. ConstructGameDataProbe
    //   6. LoadUserPlugins
    //   7. ApplyAutoOpenEnvVar
    //   8. _tickHost install → RunFrameworkTick starts
    // ─────────────────────────────────────────────────────────────────────────

    public override void Load()
    {
        var log = new BepInExPluginLog(Log);
        log.Info($"=== {PluginName} v{PluginVersion} loaded ===");
        log.Info($"[Stellar] diagnostics={(StellarDiagnostics.IsEnabled ? "ON" : "OFF")}");

        // Wire the SyncContainerDirtyData (method-22) delta discovery dump sink
        // to the BepInEx log. SetDiagnosticLog is a no-op unless
        // STELLAR_DIAGNOSTICS=1 — Infrastructure stays decoupled from IPluginLog
        // by accepting a plain Action<string> sink.
        ContainerDirtyDeltaReader.SetDiagnosticLog(log.Info);

        var typeRegistry = new ReflectionGameTypeRegistry();
        _gameTypeRegistry = typeRegistry;
        var watcher = new AppDomainHotUpdateWatcher(log);
        var hooker = new HarmonyGameMethodHooker(log, PluginGuid);

        BuildCoreServices(log, typeRegistry);
        var configFactory = BuildConfigServices(log);
        // B-04: theme stack first (NamedThemeService ctor is first inside BuildThemeAndColorStack)
        BuildThemeAndColorStack(log);
        BuildInputAndLayoutServices(log);
        BuildNativeUiServices(log);
        BuildHudServices(log);
        BuildNotificationServices(log);   // toast surface — self-owned animated ToastRenderer canvas
        BuildWindowServices(log);
        BuildLauncherServices();
        BuildInventoryServices(log, typeRegistry);
        BuildLoadoutServices(log, typeRegistry);
        // Resonance lookup must exist before the plugin-services aggregator —
        // GameAssetsService takes IGameDataResonance via its constructor. Cheap +
        // idempotent; the post-hot-update ConstructGameDataProbe shares the result.
        ConstructResonanceData(log, typeRegistry);
        WireGameEventsAndPluginHost(log, configFactory);

        watcher.WaitForAll(ExpectedHotUpdateAssemblies, () => OnHotUpdateReady(log, typeRegistry, hooker));
    }

    private void OnHotUpdateReady(
        BepInExPluginLog log,
        ReflectionGameTypeRegistry typeRegistry,
        HarmonyGameMethodHooker hooker)
    {
        log.Info("[boot] all hot-update assemblies loaded; wiring services");

        var gameType = typeRegistry.FindType(GameTypeFullName);
        if (gameType is null)
        {
            log.Error($"[boot] {GameTypeFullName} not found in any loaded assembly; aborting");
            return;
        }

        // Phase E: the IMGUI/OnGUI overlay path is gone — every Stellar surface is uGUI. All that
        // remains is the perf-overlay uGUI window + its Shift+End toggle.
        SetupPerfOverlay();
        WirePhase9Ui(log);
        // SP1 keyboard gate for uGUI windows (post-hot-update so game types resolve; ??= shares with the
        // spike). Driven per-frame from window field focus in the loop — stops the wasd leak while typing.
        _keyboardGate ??= new KeyboardInputGate(_gameTypeRegistry!, log);
        ApplyLifecyclePatches(log, typeRegistry, hooker, gameType);
        ConstructGameDataProbe(log, typeRegistry);
        LoadUserPlugins(log);
        ApplyAutoOpenEnvVar(log);

        // Drive the framework's per-frame work from the throttled InvokeRepeating ticker (NOT a
        // per-frame Game.Update postfix) so most rendered frames have zero managed entry. All
        // services are wired by now, so the tick body (RunFrameworkTick) is safe to start.
        _tickHost = new Stellar.Infrastructure.Unity.UnityTickHost(this, log);
        _tickHost.Install(RunFrameworkTick);
    }

    // Phase E: the perf overlay is a uGUI window (registered in WirePhase9Ui once _windowService
    // exists). Here we just construct the readout object + declare its Shift+End toggle hotkey.
    // There is no longer any OnGUI sink, overlay host, or per-OnGUI ThemeRenderer.Initialise —
    // the uGUI theme assets (WindowThemeAssets / HudThemeAssets) bake themselves on demand.
    private void SetupPerfOverlay()
    {
        _perfOverlay = new PerfOverlayWindow();   // registered as a uGUI window in Phase 9 (needs _windowService)
        _hotkeyService?.DeclareAction(
            new HotkeyAction(
                Id: "framework.perf-toggle",
                Description: "Toggle Perf overlay",
                SuggestedDefault: new KeyBinding(StellarKeyCode.End, ModifierKeys.Shift)),
            callback: () => { if (_perfOverlayControl != null) _perfOverlayControl.SetVisible(!_perfOverlayControl.IsShown); });
    }

    private void ApplyLifecyclePatches(
        BepInExPluginLog log,
        ReflectionGameTypeRegistry typeRegistry,
        HarmonyGameMethodHooker hooker,
        Type gameType)
    {
        InstallWireAndStubProbes(log, typeRegistry);
        HookGameLifecycleMethods(log, hooker, gameType);
    }

    /// <summary>
    /// <c>STELLAR_AUTO_OPEN=&lt;id,id,…&gt;</c> — open these windows immediately
    /// after plugin discovery + window registration completes. Used by
    /// <c>tools/run-scenario.sh</c> visual capture scenarios to drive the UI
    /// into a captured state without hotkey simulation. Read once at boot;
    /// changing the env var mid-run has no effect — restart the game.
    /// Silent no-op when unset.
    /// </summary>
    private void ApplyAutoOpenEnvVar(BepInExPluginLog log)
    {
        var autoOpen = Environment.GetEnvironmentVariable("STELLAR_AUTO_OPEN");
        if (string.IsNullOrEmpty(autoOpen)) return;
        if (_windowService is null) return;

        var ids = autoOpen.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawId in ids)
        {
            var id = rawId.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            var handle = _windowService.Find(id);   // Phase E: windows are uGUI now
            if (handle is null)
            {
                log.Warning($"[Stellar] STELLAR_AUTO_OPEN: window '{id}' not found");
                continue;
            }
            if (!handle.IsShown) handle.SetVisible(true);
            log.Info($"[Stellar] STELLAR_AUTO_OPEN: opened '{id}'");
        }
    }

    /// <summary>
    /// Returns <paramref name="mockFactory"/>'s product when
    /// <paramref name="envVarName"/> equals <c>"1"</c>; otherwise returns the
    /// production <paramref name="production"/> instance. Logs once on the
    /// mock branch so the visual-capture toolkit can grep the BepInEx log to
    /// confirm activation. See Phase 9a.5 visual verification toolkit.
    /// </summary>
    /// <remarks>
    /// This method intentionally lives in the composition root (Host). Selecting
    /// which implementation to wire — real probe vs visual-capture mock, driven by
    /// an env var — is a composition-root concern. Moving this decision to an
    /// Infrastructure factory would invert the responsibility: Infrastructure would
    /// need to know about Host's env-var convention. Keep it here.
    /// </remarks>
    private static T SelectMockOrReal<T>(
        string envVarName,
        Func<T> mockFactory,
        T production,
        BepInExPluginLog log) where T : class
    {
        var value = Environment.GetEnvironmentVariable(envVarName);
        if (value != "1") return production;
        var mock = mockFactory();
        log.Info($"[Stellar] {envVarName}=1 — using {mock.GetType().Name}");
        return mock;
    }

    /// <summary>
    /// Host shutdown hook. Restores any mutated native UI elements to their
    /// original poses, disposes every live plugin via the registry, and
    /// destroys baked theme textures so a subsequent reload doesn't leak
    /// the Texture2D objects. Best-effort under BepInEx 6 IL2CPP + Wine —
    /// process hard-kill bypasses this entirely, but orderly shutdowns
    /// (game exit, BepInEx reload) reach it.
    /// </summary>
    public override bool Unload()
    {
        var log = new BepInExPluginLog(Log);
        try { DisposePhase9(); }
        catch (Exception ex) { log.Warning($"[Bootstrap] Phase9 dispose threw: {ex.GetType().Name}: {ex.Message}"); }
        try { _grpcTeamNtfDispatcher?.Uninstall(); }
        catch (Exception ex) { log.Warning($"[Bootstrap] GrpcTeamNtfDispatcher Uninstall threw: {ex.GetType().Name}: {ex.Message}"); }
        try { _wireTap?.DisposeCapture(); }
        catch (Exception ex) { log.Warning($"[Bootstrap] WireTap DisposeCapture threw: {ex.GetType().Name}: {ex.Message}"); }
        try { _pluginRegistry?.DisposeAll(); }
        catch (Exception ex) { log.Warning($"[Bootstrap] PluginRegistry DisposeAll threw: {ex.GetType().Name}: {ex.Message}"); }
        try { _themeRenderer?.DestroyBakedTextures(); }
        catch (Exception ex) { log.Warning($"[Bootstrap] ThemeRenderer DestroyBakedTextures threw: {ex.GetType().Name}: {ex.Message}"); }
        return base.Unload();
    }
}
