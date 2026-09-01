using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Loadout services (Wiring.Loadout.cs) ────────────────────────────────
    private PandaLoadoutProbe? _loadoutProbe;
    private LoadoutService? _loadoutService;
    // Deep-Slumber write verbs (enable line / socket / unsocket a factor) — drives the raw worldProxy
    // RPCs (Approach A) over the SAME Lua bridge shape as the loadout probe. Self-resolves lazily;
    // drained world-gated in DrainEquipAndLoadout (Wiring.ServiceTick.cs) alongside the loadout probe.
    private PandaSeasonTalentProbe? _seasonTalentWriteProbe;
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

        // Deep-Slumber Psychoscope (season cultivate) — the SAME loadout probe (IDeepSlumberProbe) reads
        // it via the Lua bridge's on-demand refresh chunk (DSLV/DSA rows), NOT the C# CharSerialize
        // reflection mirror PandaInventoryProbe used to serve this from: that mirror populates LAZILY
        // (empty until the player opens the Psychoscope UI this session), so a fresh-session archive
        // uploaded no Deep-Slumber block. The Lua mirror is populated at login (owner-verified 2026-08-19).
        // Writes (line enable / factor socket-unsocket) go through the dedicated write probe — raw
        // worldProxy RPCs (Approach A), a separate Lua-bridge resolution + dispatch queue from the read
        // side (docs/driving-game-actions.md § CONFIRMED spike 2026-08-24).
        _seasonTalentWriteProbe = new PandaSeasonTalentProbe(log, typeRegistry);
        _deepSlumberService = new DeepSlumberService(_loadoutProbe, _seasonTalentWriteProbe);
        // While a plugin DS apply is in flight, the loadout probe defers its full-container refresh walk
        // so the per-op CharSerialize burst collapses into ONE refresh after the apply settles (owner
        // 2026-09-01: reset+rebuild fired the walk 3-5× → frame hitches). Narrow: true only during a
        // plugin-driven ApplySetupAsync, so a manual in-game switch is unaffected.
        _loadoutProbe.DsWriteInFlightProbe = () => _seasonTalentWriteProbe!.HasPendingWrites;

        // Self equipped Battle Imagines (IResonanceState) — the SAME loadout probe (IResonanceProbe)
        // reads cs.resonance.installed via the Lua bridge's refresh chunk ("RES" row), NOT the C#
        // CharSerialize reflection mirror PandaInventoryProbe used to serve this from: that mirror is
        // a stale latch — after an in-session imagine swap it kept serving the PRE-SWAP pair (owner
        // staging run sea/445626427740520448, 2026-08-23 — third organ of the stale-mirror disease,
        // docs/recon/combatmeter-data-facts.md). The Lua mirror is replaced wholesale on every
        // field-28 dirty delta, which now also fires SelfGearChanged → OnGearChanged → re-refresh.
        _resonanceService = new ResonanceService(_loadoutProbe);

        // Party-id reconnect refresher (built here for the shared typeRegistry + Lua bridge; _partyService
        // and _dungeonStateService are already constructed in BuildCoreServices). Reads the live run id +
        // party id each tick and, in a dungeon with no party id yet, fires WorldProxy.GetTeamInfo.
        _teamInfoRefreshProbe = new PandaTeamInfoRefreshProbe(
            log, typeRegistry,
            () => _dungeonStateService!.CurrentRunId,
            () => _partyService!.PartyId);
    }
}
