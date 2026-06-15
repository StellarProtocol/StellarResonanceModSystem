using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>
/// Immutable snapshot of all module-package items currently held by the
/// player. Produced by <c>IInventoryProbe.TryReadModules</c> at the 1Hz
/// refresh tick and surfaced via
/// <see cref="Stellar.Abstractions.Services.IInventory.GetModules"/>.
/// </summary>
public sealed record ModuleSnapshot(
    IReadOnlyList<ModuleInfo> Modules,
    long ServerSampledAtTicks);
