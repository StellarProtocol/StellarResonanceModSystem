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

    /// <summary>
    /// Raised on the game tick AFTER the framework re-read the local player's LIVE build state and
    /// the re-read actually CHANGED what this service serves — equipped gear/module slots, class,
    /// talent stage/nodes, the equipped Battle Imagine pair, or the Deep-Slumber Psychoscope state
    /// (<see cref="IDeepSlumber.GetState"/>). An identical re-read raises nothing.
    ///
    /// <para><b>ONE event for the whole build.</b> Deep-Slumber joined this event 2026-08-23 (owner
    /// staging run <c>sea/dXkw1PSyOG</c>: a psychoscope factor was unequipped between two archives and
    /// re-equipped after; the framework re-read it correctly but told nobody, so the consumer kept one
    /// stale snapshot for two materially different builds). Subscribers therefore never need a second
    /// subscription — or a poll — to notice a psychoscope edit; re-read whatever build surfaces they
    /// snapshot, <see cref="IDeepSlumber"/> included, whenever this fires. Both compares are
    /// STRUCTURAL and order-insensitive, and a not-yet-read surface is treated as no-signal (it never
    /// raises on its own).</para>
    ///
    /// <para><b>Why this and not <see cref="IInventory.SelfGearChanged"/>:</b> that event fires on the
    /// network thread the instant a container delta ARRIVES, which is BEFORE the framework has re-read
    /// the game's live containers — a consumer that snapshots the build from that handler's tick races
    /// the refresh and records the PRE-change setup. This event is the post-parse counterpart: by the
    /// time it fires, <see cref="GetSlots"/>, <see cref="LiveState"/> and
    /// <see cref="IResonanceState.Installed"/> already describe the new setup, so a consumer can flag
    /// here and snapshot on its next update tick.</para>
    ///
    /// <para><b>That promise is structural, not incidental (2026-08-23).</b> The framework re-reads the
    /// live slot→item line and RESOLVES those items into served gear/modules in two separate steps; the
    /// event is published from the SECOND one, so it can never fire while <see cref="GetSlots"/> still
    /// describes the previous setup. If the resolve cannot complete (item container not synced yet), the
    /// change is held and delivered on the tick the data lands — LATE, never STALE. A consumer may
    /// therefore treat this event as "the setup I can read right now is the new one".</para>
    ///
    /// <para><b>Threading:</b> raised on the game Update thread (unlike
    /// <see cref="IInventory.SelfGearChanged"/>), so a handler may read game-backed services directly.
    /// Keep handlers short — this runs inside the framework service tick.</para>
    /// </summary>
    event Action? LiveStateChanged;
}
