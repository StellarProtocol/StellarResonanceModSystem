using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private void BuildInventoryServices(BepInExPluginLog log, ReflectionGameTypeRegistry typeRegistry)
    {
        // Phase 7 Iter 1: real IInventory backed by PandaInventoryProbe. The
        // probe resolves lazily on first read, so construction is safe even
        // before HybridCLR has loaded the hot-update assemblies. The 1Hz
        // refresh is driven from RefreshPerTickServices below.
        // Self equipped gear — the wire capture decodes the method-21 full
        // sync via GearInstanceReader and replaces this volatile-swap cache;
        // InventoryService serves it through IInventory.GetSelfGear.
        var selfGearCache = new SelfGearCache();
        _inventoryProbe = new PandaInventoryProbe(log, typeRegistry, selfGearCache);
        _inventoryService = new InventoryService(_inventoryProbe, selfGearCache, log);

        // Self equipped Battle Imagines — the same probe (IResonanceProbe) reads
        // CharSerialize.resonance (field 28) off the same latched CharSerialize.
        _resonanceService = new ResonanceService(_inventoryProbe);

        // Phase 7 Iter 2: real IModuleEquip backed by PandaModuleEquipProbe.
        // It dispatches equip/uninstall through the game's Lua VM
        // (ModVM.AsyncEquipMod via the LuaState bridge) and polls the inventory
        // probe's captured Mod.ModSlots map for completion (B2). The Lua-bridge
        // reflection resolves lazily after HybridCLR loads ZLuaFramework.
        _moduleEquipProbe = new PandaModuleEquipProbe(
            log, typeRegistry, () => _inventoryProbe.GetEquippedSlotsForEquipPolling());
        _moduleEquipService = new ModuleEquipService(_moduleEquipProbe);
    }
}
