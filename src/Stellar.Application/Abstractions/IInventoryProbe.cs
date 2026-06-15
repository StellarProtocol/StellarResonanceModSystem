using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound contract for reading inventory state from the game. Implemented
/// by Infrastructure (<c>PandaInventoryProbe</c>) via a HarmonyX capture-hook
/// on <c>Panda.ZGame.ContainerSyncService.SyncContainerData</c>, which latches
/// the live <c>CharSerialize</c> the server pushes on each container sync.
/// Application's <c>InventoryService</c> consumes this without ever touching
/// IL2CPP.
///
/// Both methods return <c>false</c> rather than throwing when the game's
/// container path can't be resolved yet — Application treats this as
/// "data not ready", not as an error.
/// </summary>
internal interface IInventoryProbe
{
    bool TryReadModules(out ModuleSnapshot snapshot);

    bool TryReadEquipped(out EquippedSet equipped);
}
