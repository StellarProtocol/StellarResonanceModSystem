using System.IO;
using Stellar.Abstractions.Services;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Configuration;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private void WireGameEventsAndPluginHost(BepInExPluginLog log, PluginConfigFactory configFactory)
    {
        BuildUGuiAdapters(log);
        ConstructPluginServices(log, configFactory);
        WireFrameworkUpdateEvents();
    }

    /// <summary>
    /// Constructs the uGUI injection adapter + service and wires the three
    /// per-tick Update delegates. Must run before <see cref="ConstructPluginServices"/>
    /// because <c>_uguiInjection</c> feeds the PluginServices aggregator.
    /// </summary>
    private void BuildUGuiAdapters(BepInExPluginLog log)
    {
        // Phase 9d: declarative mod-uGUI injection into game canvases. The adapter
        // resolves anchors lazily at Tick, so it's safe to build this early.
        var uguiAdapter = new Stellar.Infrastructure.Game.PandaUGuiAdapter(log, _themeRenderer!);
        var uguiInjection = new Stellar.Application.Services.UGuiInjectionService(uguiAdapter);
        _uguiInjection = uguiInjection;
        _framework!.Update += dt => uguiInjection.Tick(dt);
        _framework!.Update += _ => uguiAdapter.TickGlow();   // per-frame rail-glow pulse
        _framework!.Update += _ => _menuState?.Tick();
    }

    /// <summary>
    /// Selects real vs mock implementations (visual-capture toolkit), constructs
    /// the 25-param <see cref="PluginServices"/> aggregator, the
    /// <see cref="PluginRegistry"/> (fully initialised in one step — B-05), and
    /// the <see cref="PluginHost"/>. Must run after <see cref="BuildUGuiAdapters"/>
    /// (needs <c>_uguiInjection</c>).
    /// </summary>
    private void ConstructPluginServices(BepInExPluginLog log, PluginConfigFactory configFactory)
    {
        // Phase 9a.5 visual verification toolkit: three env vars can swap
        // production services for deterministic mocks so visual scenarios can
        // render CooldownBar / PlayerHUD / StatInspector / ModuleOptimizer
        // outside of real gameplay (title screen, character select). Silent
        // no-op when env vars are absent — production probes + services still
        // construct and tick; only the aggregator-input interface changes.
        ICombatSnapshot combatSnapshot = SelectMockOrReal<ICombatSnapshot>(
            "STELLAR_MOCK_COOLDOWNS", static () => new MockCombatSnapshot(), _combatService!, log);
        IPlayerState playerState = SelectMockOrReal<IPlayerState>(
            "STELLAR_MOCK_STATS", static () => new MockPlayerState(), _playerState!, log);
        IInventory inventory = SelectMockOrReal<IInventory>(
            "STELLAR_MOCK_INVENTORY", static () => new MockInventory(), _inventoryService!, log);

        var gameAssets = new GameAssetsService(log, _gameDataService!.Combat, _gameDataResonance!, _gameDataService!.Inventory);
        // Party-size control bridge (Lua → game's own ChangeTeamMemberType). Lazy-resolves in-world.
        _teamControlProbe = new PandaTeamControlProbe(_gameTypeRegistry!, log);
        _partyControlService = new PartyControlService(_teamControlProbe);
        // Portrait pipeline: Lua bridge creates the social-data model; the host renders it via the game's own
        // ZModel2RT render feature (the only path that isolates one model — the SRP renders the world globally).
        var portraitModelProbe = new PandaPortraitModelProbe(_gameTypeRegistry!, log);
        var portraitModelHost = new PortraitModelHost(_gameTypeRegistry!, log);
        var services = new PluginServices(log, _framework!, _clientState!, _gameDataService!,
            _playerStatsService!, inventory, _moduleEquipService!, _pluginConfigService!,
            _gameEvents!, playerState, _chatService!,
            combatSnapshot, _combatService!, _combatService!,
            _partyService!, _partyService!, _partyService!, _partyControlService!,
            _themeRenderer!, _hotkeyService!,
            _namedTheme!, _uguiInjection!, _hudService!, _windowService!, _launcher!,
            gameAssets, _resonanceService!, _gameDataResonance!,
            _combatService!,
            new Stellar.Application.Services.EntityContextMenuService(),
            new Stellar.Infrastructure.Game.EntityPortraitService(portraitModelProbe, portraitModelHost),
            _profileCardActions!,
            new Stellar.Application.Services.PluginExchange());
        _capturedServices = services;
        WireProfileCardActionInjector(log);

        // PluginRegistry constructed after the aggregator so it is fully initialised
        // in one step — no late-bind / SetServices (B-05).
        var pluginsSection = _pluginConfigService!.GetSection("plugins");
        _pluginRegistry = new PluginRegistry(pluginsSection, log, services);

        _pluginHost = new PluginHost(services, configFactory, _pluginRegistry);
    }

    /// <summary>
    /// Native profile-card action injector: builds one styled button per registered
    /// <c>ProfileCardActionSpec</c> (<see cref="ProfileCardActionRegistry"/>, read via
    /// <c>IProfileCardActionSource</c>) into the card's action bar and, on click, resolves the carded
    /// player's charId (Lua: <c>Z.UIMgr:GetView("idcard").cardId_</c>) and invokes the spec's OnClick.
    /// Ticked from the framework Update so it injects once per card-open; the buttons die with the card.
    /// </summary>
    private void WireProfileCardActionInjector(BepInExPluginLog log)
    {
        _profileCardActionInjector = new Stellar.Infrastructure.Game.PandaProfileCardActionInjector(
            _gameTypeRegistry!, _profileCardActions!, log);
        _framework!.Update += _ => _profileCardActionInjector.Tick();
    }

    /// <summary>
    /// Attaches the two <see cref="GameEventsService"/> bridges in priority order
    /// (first to succeed wins). Called after <see cref="ConstructPluginServices"/>
    /// so services are live when event traffic starts.
    /// </summary>
    private void WireFrameworkUpdateEvents()
    {
        // Bridges are tried in this order; first to succeed wins.
        _gameEvents!.AttachBridge(_messagePipeBridge!);
        _gameEvents.AttachBridge(_harmonyBridge!);
    }

    private void LoadUserPlugins(BepInExPluginLog log)
    {
        var pluginDirectory = Path.Combine(BepInEx.Paths.GameRootPath, UserPluginSubdirectory);
        log.Info($"[boot] loading user plugins from {pluginDirectory}");
        _pluginHost!.LoadFrom(pluginDirectory);
    }
}
