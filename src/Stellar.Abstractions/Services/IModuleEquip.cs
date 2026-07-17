using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Game-state mutation primitive: equip / uninstall modules via the
/// game's own RPC dispatcher. Implementations invoke the game's Lua
/// functions <c>ModVM.AsyncEquipMod</c> / <c>ModVM.AsyncUninstallMod</c>
/// through the <c>ZLuaFramework</c> bridge — the plugin supplies inputs,
/// the game's Lua code builds the protobuf and applies its own validation.
///
/// User-initiated only. Plugins MUST trigger calls from a user action
/// (button press, slash command, hotkey) — not from a timer, scheduled
/// event, or background loop. See the project's out-of-scope policy (README).
///
/// Async: every call polls the game's <c>Mod.ModSlots</c> map until it
/// reflects the requested change, or times out at 6 seconds (matching the
/// Lua proxy timeout). Callers must <c>await</c> and check the returned
/// <see cref="EquipResult"/>.
/// </summary>
public interface IModuleEquip
{
    /// <summary>True when the game's RPC dispatcher has been resolved
    /// AND a real player entity is in-world. False during boot, character
    /// select, or zone transition.</summary>
    bool IsAvailable { get; }

    /// <summary>Invokes <c>ModVM.AsyncEquipMod(moduleUuid, slotId)</c>.
    /// Slot is 1..<c>ModSlotMaxCount</c> (4 before patch 3.7, 5 since); the
    /// framework imposes no cap — the game validates the slot id. The Task
    /// completes when the game's <c>Mod.ModSlots</c> map reflects the
    /// equip or the 6-second timeout elapses.</summary>
    Task<EquipResult> InstallAsync(int slotId, long moduleUuid, CancellationToken ct = default);

    /// <summary>Invokes <c>ModVM.AsyncUninstallMod(slotId)</c>.
    /// Returns <see cref="EquipResult.SlotEmpty"/> (not an error) if the
    /// slot held no module.</summary>
    Task<EquipResult> UninstallAsync(int slotId, CancellationToken ct = default);
}
