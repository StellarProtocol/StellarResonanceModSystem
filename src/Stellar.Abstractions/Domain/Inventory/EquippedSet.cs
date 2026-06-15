using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>
/// Currently equipped module UUIDs keyed by 1-based slot index. The game
/// uses 1..<c>ModSlotMaxCount</c> = 1..4 (see
/// <c>lua/ui/model/mod_define.lua</c>). Empty slots are ABSENT from the
/// dictionary — callers should check
/// <c>ModuleUuidsBySlot.ContainsKey(slot)</c> rather than expect a
/// nullable value.
/// </summary>
public sealed record EquippedSet(IReadOnlyDictionary<int, long> ModuleUuidsBySlot);
