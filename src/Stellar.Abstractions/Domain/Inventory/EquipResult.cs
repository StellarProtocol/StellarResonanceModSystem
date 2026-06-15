namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>
/// Outcome of an <see cref="Stellar.Abstractions.Services.IModuleEquip"/>
/// dispatch. Maps the game's <c>EErrorCode</c> reply to plugin-friendly
/// cases — plugins should never need to read raw error codes.
/// </summary>
public enum EquipResult
{
    /// <summary>Game accepted the request; matching event fired.</summary>
    Success,

    /// <summary>Reflection couldn't resolve the game's Lua bridge
    /// (<c>ZLuaFramework</c> / <c>ModVM</c>). Typically appears at boot
    /// before HybridCLR finishes loading.</summary>
    GameApiUnavailable,

    /// <summary>No live player entity (character select / loading screen).</summary>
    PlayerNotInWorld,

    /// <summary>Game returned slot-not-unlocked (Tips lang 1042105).</summary>
    SlotLocked,

    /// <summary>Game returned mod-type conflict or category cap exceeded
    /// (Tips lang 1042106 / 1042110).</summary>
    SlotConflict,

    /// <summary>Returned by <see cref="Stellar.Abstractions.Services.IModuleEquip.UninstallAsync"/>
    /// when the target slot was already empty. Not an error.</summary>
    SlotEmpty,

    /// <summary>The supplied module UUID isn't in the player's current
    /// inventory snapshot.</summary>
    ModuleNotInInventory,

    /// <summary>Server-side EErrorCode was non-zero but didn't match
    /// any specific case.</summary>
    RpcError,

    /// <summary>6 seconds elapsed without the matching event firing
    /// (matches the Lua <c>coro_util.async_to_sync(..., 6)</c> timeout).</summary>
    Timeout,

    /// <summary>Caller's CancellationToken fired before completion.</summary>
    Cancelled,
}
