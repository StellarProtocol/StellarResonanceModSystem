using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-only access to the player's module-package inventory and currently
/// equipped set. Sampled at 1Hz from the Game.Update tick; not subscription
/// driven (inventory changes are infrequent enough that 1Hz polling beats
/// the bookkeeping cost of subscriptions).
///
/// Pair with <see cref="IGameDataCombat.GetAttributeProfile"/> to resolve
/// <c>ModulePart.AttrId</c> labels (Phase 6 reuse).
///
/// Threading: <see cref="GetModules"/> and <see cref="GetEquipped"/> are
/// thread-safe lock-free reads (Volatile under the hood). The
/// <see cref="InventoryChanged"/> event raises on the framework Update
/// thread; subscribers can call IMGUI from the handler. <see cref="SelfGearChanged"/>
/// is different — it raises on the network/sync thread (see its remarks).
/// </summary>
public interface IInventory
{
    /// <summary>True after the first successful probe sample. False
    /// during character select / loading / before HybridCLR finishes.</summary>
    bool IsAvailable { get; }

    /// <summary>Current module inventory snapshot, or null until the
    /// first sample lands.</summary>
    ModuleSnapshot? GetModules();

    /// <summary>Currently equipped module UUIDs by slot (1..<c>ModSlotMaxCount</c>;
    /// 4 before patch 3.7, 5 since). Slots with no module are absent from the
    /// returned dictionary.</summary>
    EquippedSet? GetEquipped();

    /// <summary>The LOCAL player's currently equipped gear instances with
    /// their per-piece rolled attributes, ordered by slot. Empty (never null)
    /// until the first full container sync lands. Staleness contract: the
    /// list is refreshed on full container syncs (login / map change);
    /// mid-session refines or re-rolls may be stale until the next full sync
    /// (method-22 incremental deltas are not decoded for gear). Thread-safe
    /// lock-free read, like the other getters.</summary>
    IReadOnlyList<GearInstance> GetSelfGear();

    /// <summary>Fires once when the snapshot or equipped set diffs the
    /// previous tick. Polling cadence is 1Hz so the event fires at most
    /// once per second.</summary>
    event Action? InventoryChanged;

    /// <summary>Fires when the LOCAL player's self-gear is (re)synced — i.e. a full
    /// container sync landed and <see cref="GetSelfGear"/> now returns fresh data.
    /// This is the ONLY signal for a self-gear change: <see cref="InventoryChanged"/>
    /// deliberately excludes self-gear (it is push-fed, not part of the 1Hz probe hash).
    /// A class swap re-syncs the new class's gear a moment after the profession flips,
    /// so consumers that snapshot per-class gear should re-read on this event rather than
    /// at the profession-change instant (the gear is stale then).
    /// <para><b>Threading:</b> raised on the NETWORK/SYNC thread, NOT the game Update
    /// thread. Handlers MUST be minimal and thread-safe (e.g. set a volatile flag) and
    /// MUST NOT read game state (IL2CPP) from the handler — defer that to the game tick.</para></summary>
    event Action? SelfGearChanged;
}
