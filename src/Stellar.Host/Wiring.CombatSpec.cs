using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;

namespace Stellar.Host;

/// <summary>
/// Spec-from-talent-buffs wiring (2026-09-26): the buff id → spec id map shared by <c>CombatService</c>'s
/// <c>ICombatSpec</c>. It starts on the built-in constants and is derived from the game's own talent tables
/// once the deferred game-data drain has finished (same world-gated game-thread tick, so the table reads
/// happen where every other table read does).
/// </summary>
public sealed partial class BootstrapPlugin
{
    private SpecRootBuffMap? _specRootBuffMap;

    private SpecRootBuffMap BuildSpecRootBuffMap(BepInExPluginLog log) => _specRootBuffMap = new SpecRootBuffMap(log);

    private void LoadSpecRootBuffs()
    {
        if (_specRootBuffMap is null || _gameDataProbe is null) return;
        _specRootBuffMap.LoadFrom(_gameDataProbe);
    }
}
