using System.Collections.Generic;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound: the Infrastructure container-sync capture pushes the LOCAL
/// player's decoded equipped-gear instances here on each method-21
/// <c>SyncContainerData</c> full sync. Implemented by
/// <c>SelfGearCache</c>. Calls arrive on the network receive thread;
/// implementations must publish with a thread-safe swap.
/// </summary>
internal interface IGearInstanceSink
{
    /// <summary>Replace (never merge) the cached self-gear list with the
    /// freshly decoded full-sync result. Full syncs are authoritative.</summary>
    void OnGearSync(IReadOnlyList<GearInstance> gear);

    /// <summary>Signals the equipped set changed via a method-22 dirty delta (a manual gear/module equip,
    /// refine, or a class-swap re-equip) WITHOUT decoding new gear here — raises the change event so a
    /// consumer can re-read the LIVE container. Distinct from <see cref="OnGearSync"/> (which replaces the
    /// full-sync cache). Same threading contract: network/sync thread.</summary>
    void OnGearMaybeChanged();
}
