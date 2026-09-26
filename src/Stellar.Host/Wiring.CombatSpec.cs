using Stellar.Abstractions.Diagnostics;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;

namespace Stellar.Host;

/// <summary>
/// Spec-from-talent-buffs wiring (2026-09-26): the buff id → spec id map shared by <c>CombatService</c>'s
/// <c>ICombatSpec</c>. It starts on the built-in constants and is derived from the game's own talent tables
/// once the deferred game-data drain has finished — one table per world-gated game-thread tick, like every
/// other deferred table read.
/// </summary>
public sealed partial class BootstrapPlugin
{
    private SpecRootBuffMap? _specRootBuffMap;
    private bool _specRootBuffsLoaded;

    private SpecRootBuffMap BuildSpecRootBuffMap(BepInExPluginLog log) => _specRootBuffMap = new SpecRootBuffMap(log);

    // One talent table per tick, after the deferred game-data drain has finished (world-gated like it).
    [WorldGated]
    private void StepSpecRootBuffs()
    {
        if (!_gameDataAllLoaded || _specRootBuffsLoaded || _specRootBuffMap is null || _gameDataProbe is null) return;
        if (!_clientState!.IsWorldActive) return;   // reads live game tables — never during the connect handshake
        _specRootBuffsLoaded = _specRootBuffMap.LoadStep(_gameDataProbe);
    }
}
