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

    /// <summary>Current self-gear list; empty until the first full sync.</summary>
    public IReadOnlyList<GearInstance> Current => Volatile.Read(ref _gear);

    public void OnGearSync(IReadOnlyList<GearInstance> gear)
        => Volatile.Write(ref _gear, gear ?? Array.Empty<GearInstance>());
}
