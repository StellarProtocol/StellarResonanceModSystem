using System.IO;
using BepInEx;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Configuration;
using Stellar.Infrastructure.Events;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private void BuildCoreServices(BepInExPluginLog log, ReflectionGameTypeRegistry typeRegistry)
    {
        _framework = new FrameworkService();
        _scheduler = new Stellar.Application.Services.TickScheduler(
            maxHoldSeconds: 10.0,
            log: m => log.Info(m));
        _scheduler.SetGlobalRate(Stellar.Abstractions.Diagnostics.PerfControls.UpdateRateHz);
        _clientState = new ClientStateService();
        _harmonyBridge = new HarmonyEventBridge();
        _messagePipeBridge = new MessagePipeContainerBridge(log, typeRegistry);
        _gameEvents = new GameEventsService(log);
        _playerState = new PlayerStateService();
        // CombatEntityTracker is shared between CombatService (writes) and
        // GameDataWorldService (reads attr-10 for GetMonsterByEntity). Stored on
        // BootstrapPlugin so the two construction sites can reference the same instance.
        _entityTracker = new CombatEntityTracker();
        _gameDataService = new GameDataService(_entityTracker);
        _playerStatsService = new PlayerStatsService();
        _playerStateProbe = new PandaPlayerStateProbe(log, typeRegistry);
        _playerStatsProbe = new PandaPlayerStatsProbe(log, _playerStateProbe);
        _chatService = new ChatService(log);
        // Per-player social-data cache: the read side feeds IEntityDetail.GetSocialSnapshot (consumed
        // by CombatService) and the same instance is the ISocialDataSink the Infrastructure wire tap
        // pushes decoded GetSocialDataReply records into.
        _socialDataCache = new SocialDataCache();
        // Generic profile-card action registry: plugins register buttons (write side); the native-card
        // injector reads the registered set (IProfileCardActionSource) and injects one per action per open.
        _profileCardActions = new ProfileCardActionRegistry();
        _combatService = new CombatService(log, _entityTracker, _socialDataCache);
        _partyService = new PartyService(_combatService, _clientState, log);
        // Dungeon run state (WorldNtf SyncDungeonData → IDungeonState). Read+write
        // sides on one service; the Infrastructure probe (Wiring.Wire.cs) pushes via IDungeonStateSink.
        _dungeonStateService = new DungeonStateService();
        // B-01: frame-rate uncap / vSync reconciler. Diff-state + Unity writes live in Infrastructure;
        // the IFrameRateLimiter port keeps QualitySettings out of Host's tick body.
        _frameLimiter = new Stellar.Infrastructure.Unity.FrameRateReconciler(log);
    }

    private PluginConfigFactory BuildConfigServices(BepInExPluginLog log)
    {
        var pluginsDirPath = Path.Combine(Paths.GameRootPath, UserPluginSubdirectory);
        _configStore = new FileConfigStore(log, pluginsDirPath);
        // Framework-owned config (currently unused by sample plugins — they each
        // get their own per-plugin config via the PluginConfigFactory below).
        _pluginConfigService = new PluginConfigService(_configStore, PluginGuid);
        return new PluginConfigFactory(_configStore);
    }
}
