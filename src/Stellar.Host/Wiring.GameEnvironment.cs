using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private GameEnvironmentService? _gameEnvironment;

    // Region + game-version identity of this install (SEA/JP), detected once at
    // boot from install markers; `environment.region` in the framework config
    // overrides. Must run after BuildConfigServices (needs the config section).
    private void BuildGameEnvironment(BepInExPluginLog log)
    {
        var section = _pluginConfigService!.GetSection("environment");
        _gameEnvironment = new GameEnvironmentService(new BepInExInstallInfo(), section);
        log.Info($"[Stellar] region={_gameEnvironment.RegionCode} version={_gameEnvironment.GameVersion} source={_gameEnvironment.RegionSource}");
    }
}
