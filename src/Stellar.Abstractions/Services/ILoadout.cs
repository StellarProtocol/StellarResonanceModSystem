using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;

namespace Stellar.Abstractions.Services;

/// <summary>Read + apply the player's saved in-game loadouts (class + gear + spec + modules).
/// Applying drives the game's own switch and surfaces the game's result; it never bypasses
/// game-side validation (combat lock, profession/weapon match).</summary>
public interface ILoadout
{
    /// <summary>True once the game-side loadout API has been resolved and is callable.</summary>
    bool IsAvailable { get; }

    /// <summary>The saved loadout slots, in the game's dropdown order. Empty until <see cref="IsAvailable"/>.</summary>
    IReadOnlyList<LoadoutSlot> GetSlots();

    /// <summary>Index of the currently-active loadout, or null if none/unknown.</summary>
    int? CurrentIndex { get; }

    /// <summary>The local player's LIVE class + talents (never from a saved plan), or null until the
    /// live read resolves in-world. Refreshed with the loadout data; a talent respec re-fires the
    /// refresh via the framework's dirty-delta trigger, so re-read after
    /// <see cref="IInventory.SelfGearChanged"/>.
    /// Same threading rule as <see cref="IDeepSlumber.GetState"/>: never read from the
    /// <see cref="IInventory.SelfGearChanged"/> handler itself (network thread) — flag and read on the
    /// game tick. The value read immediately after the event may still be pre-change; the refresh
    /// lands within about a second (cooldown-coalesced), which is fine for snapshot-at-archive use.</summary>
    LiveLoadoutState? LiveState { get; }

    /// <summary>Triggers the game's native switch to the loadout identified by <paramref name="index"/>.</summary>
    /// <param name="index">A <see cref="LoadoutSlot.Index"/> value.</param>
    /// <param name="ct">Cancels the request before dispatch.</param>
    /// <returns>The game's outcome for the switch.</returns>
    Task<LoadoutResult> ApplyAsync(int index, CancellationToken ct = default);

    /// <summary>Raised when the saved-loadout list or the current selection changes.</summary>
    event Action? LoadoutsChanged;
}
