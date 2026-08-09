using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Volatile-swap cache for the LOCAL player's equipped gear instances.
/// Fed by the Infrastructure container-sync capture via
/// <see cref="IGearInstanceSink"/> on each method-21 full sync; read by
/// <see cref="InventoryService.GetSelfGear"/>. Every sync REPLACES the whole
/// list (full syncs are authoritative — evict-and-replace, never merge).
/// Writes land on the network receive thread, reads on any thread; both
/// sides are lock-free Volatile operations on the single list reference.
/// </summary>
internal sealed class SelfGearCache : IGearInstanceSink
{
    private IReadOnlyList<GearInstance> _gear = Array.Empty<GearInstance>();

    /// <summary>Raised on the network/sync thread AFTER a full sync replaces the cache —
    /// forwarded by <see cref="InventoryService"/> as <c>IInventory.SelfGearChanged</c>.</summary>
    public event Action? Changed;

    /// <summary>Current self-gear list; empty until the first full sync.</summary>
    public IReadOnlyList<GearInstance> Current => Volatile.Read(ref _gear);

    public void OnGearSync(IReadOnlyList<GearInstance> gear)
    {
        Volatile.Write(ref _gear, gear ?? Array.Empty<GearInstance>());
        Changed?.Invoke();
    }

    // Method-22 dirty delta (manual equip / refine / class-swap re-equip): the wire capture doesn't decode
    // gear from the delta, so leave the full-sync cache as-is and just fire Changed — consumers re-read the
    // LIVE container (GetLiveEquipped), which already reflects the change.
    public void OnGearMaybeChanged() => Changed?.Invoke();

    /// <summary>Empty the cache on logout (account/character-scoped session data). Uses the same
    /// volatile-swap as <see cref="OnGearSync"/> but deliberately does NOT raise <see cref="Changed"/>:
    /// a logout is a session teardown, not a live gear edit, and firing would push an empty-gear
    /// notification through InventoryService.SelfGearChanged to plugins mid-teardown. Consumers re-read
    /// <see cref="Current"/> (now empty) on their next poll.</summary>
    internal void ClearSession() => Volatile.Write(ref _gear, Array.Empty<GearInstance>());
}
