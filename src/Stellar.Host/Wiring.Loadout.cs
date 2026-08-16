using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Loadout services (Wiring.Loadout.cs) ────────────────────────────────
    private PandaLoadoutProbe? _loadoutProbe;
    private LoadoutService? _loadoutService;
    // Mid-dungeon-reconnect party-id refresher — invokes WorldProxy.GetTeamInfo({}) via the same Lua
    // bridge so a reconnected run's PartyId fills in promptly (see PandaTeamInfoRefreshProbe). Ticked
    // world-gated in DrainEquipAndLoadout (Wiring.ServiceTick.cs).
    private PandaTeamInfoRefreshProbe? _teamInfoRefreshProbe;

    /// <summary>
    /// Constructs the loadout (profession-project) switch probe + service. The
    /// probe dispatches the switch through the game's own Lua VM
    /// (<c>Z.VMMgr.GetVM(...).&lt;ApplyFn&gt;(id, token)</c> via the tolua# LuaState
    /// bridge) and polls the <c>CurrentProfessionProjectId</c> container for
    /// completion. The Lua bridge + current-id container resolve lazily after
    /// HybridCLR loads the game assemblies, so construction is safe pre-login.
    /// <see cref="LoadoutService.Tick"/> is driven from the Host service tick.
    /// </summary>
    private void BuildLoadoutServices(BepInExPluginLog log, ReflectionGameTypeRegistry typeRegistry)
    {
        _loadoutProbe = new PandaLoadoutProbe(log, typeRegistry);
        // Per-class gear/modules (2026-08-03): the loadout probe reads each saved plan's equip/mod
        // slot→uuid maps (Lua), then hands them to the inventory probe's item-container resolver to
        // surface each class's real gear/modules on LoadoutSlot (the live self-gear/module APIs are
        // class-blind). _inventoryProbe is built first (BootstrapPlugin build order), so it's ready.
        _loadoutProbe.AttachGearResolver(plans => _inventoryProbe!.ResolvePlanLoadouts(plans));
        // Live-overlay freshness: a gear/module change or a loadout switch fires SelfGearChanged (method-22
        // field-12/57 delta) → re-read the CURRENT class's live equipped set so a manual edit shows. The
        // handler only flips a flag (network-thread-safe). _inventoryService is built before this.
        _inventoryService!.SelfGearChanged += _loadoutProbe.OnGearChanged;
        _loadoutService = new LoadoutService(_loadoutProbe);

        // Party-id reconnect refresher (built here for the shared typeRegistry + Lua bridge; _partyService
        // and _dungeonStateService are already constructed in BuildCoreServices). Reads the live run id +
        // party id each tick and, in a dungeon with no party id yet, fires WorldProxy.GetTeamInfo.
        _teamInfoRefreshProbe = new PandaTeamInfoRefreshProbe(
            log, typeRegistry,
            () => _dungeonStateService!.CurrentRunId,
            () => _partyService!.PartyId);
    }
}
