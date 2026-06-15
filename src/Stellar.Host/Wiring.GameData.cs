using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // Resonance lookup + the shared MLString resolver are constructed early
    // (before the plugin-services aggregator) because GameAssetsService takes
    // IGameDataResonance via its constructor. Both are cheap — no reflection
    // invokes at construction; the resonance cache builds lazily on first access.
    private void ConstructResonanceData(BepInExPluginLog log, ReflectionGameTypeRegistry typeRegistry)
    {
        _mlStrings ??= new PandaMLStringResolver(log, typeRegistry);
        // Battle Imagine (Resonance Skill) lookup. Self-contained reflection +
        // lazy cache build on first access; reuses the shared MLString resolver.
        _gameDataResonance ??= new GameDataResonance(log, typeRegistry, _mlStrings);
    }

    private void ConstructGameDataProbe(BepInExPluginLog log, ReflectionGameTypeRegistry typeRegistry)
    {
        // Construct the probe now (cheap, no reflection invokes) but defer the
        // actual eager batch to Game.Init postfix — Bokura.*TableBase.GetTable
        // returns null until Panda.TableInitUtility.Init has populated the static
        // table handles, which happens inside Panda.Core.Game.Init.
        ConstructResonanceData(log, typeRegistry);   // idempotent — shares _mlStrings / _gameDataResonance
        _gameDataProbe = new PandaGameDataProbe(log, typeRegistry, _mlStrings!);
        _gameDataLog = log;
    }
}
