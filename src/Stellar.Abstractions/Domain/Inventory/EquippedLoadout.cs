using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.Inventory;

/// <summary>The LOCAL player's CURRENT LIVE equipped set, read from the game's live equip/mod containers
/// (<c>CharSerialize.equip.equipList</c> / <c>CharSerialize.mod.modSlots</c>). Unlike the method-21
/// self-gear cache, this reflects manual equips, refines, and class-swap re-equips — so a consumer that
/// snapshots per-class gear as each class is played captures what was ACTUALLY worn. Empty (never null
/// lists) until the container resolves.</summary>
/// <param name="Gear">Equipped gear pieces with full rolls, refine level, and enchant; ordered by slot.</param>
/// <param name="Modules">Equipped modules with rolled parts, keyed by 1-based module slot.</param>
public sealed record EquippedLoadout(
    IReadOnlyList<GearInstance> Gear,
    IReadOnlyDictionary<int, ModuleInfo> Modules);
