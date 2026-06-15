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
}
