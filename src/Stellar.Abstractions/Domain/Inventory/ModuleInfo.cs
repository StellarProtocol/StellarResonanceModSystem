using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>
/// A single module item from the player's inventory. UUID is the unique
/// instance key the game's equip RPC consumes; ConfigId is the table-row
/// key for <c>ModTableMgr</c> (look up via a future
/// <c>IGameDataInventory.GetMod</c>).
/// </summary>
public sealed record ModuleInfo(
    long Uuid,
    int ConfigId,
    string Name,
    int Quality,
    ModuleCategory Category,
    IReadOnlyList<ModulePart> Parts);
