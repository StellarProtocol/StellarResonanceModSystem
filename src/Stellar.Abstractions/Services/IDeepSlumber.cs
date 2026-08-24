using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.Abstractions.Services;

/// <summary>Read-only access to the local player's Deep-Slumber Psychoscope (season cultivate)
/// state — season level plus every cultivate line's areas, socketed cards, and node levels. The
/// state is refreshed from the game's live containers via the framework's on-demand Lua bridge
/// (on login, and again on build-state changes — including Deep-Slumber edits), then cached until
/// the next refresh; it is cleared on logout so a relog never serves the previous character's data.
/// Because the refresh is on-demand rather than synchronous with the edit, <see cref="GetState"/>
/// called immediately after an edit may briefly return the state from before that edit.
///
/// <para><b>To be told when it changes, subscribe to <see cref="ILoadout.LiveStateChanged"/></b>
/// (2026-08-23): that ONE event now also fires when the re-read Deep-Slumber state structurally
/// differs, on the game Update thread, after this service already serves the new state. Do NOT
/// use <see cref="IInventory.SelfGearChanged"/> for this — it fires on the NETWORK thread the moment a
/// container delta arrives, which is BEFORE the framework re-reads the game's containers, so a handler
/// that snapshots there records the PRE-edit state (and must never touch game services off-thread
/// anyway). Never call <see cref="GetState"/> from that handler: set a flag and read on the game
/// tick, or simply read at snapshot time.</para></summary>
public interface IDeepSlumber
{
    /// <summary>True once the live container is resolved and <see cref="GetState"/> can return data.</summary>
    bool IsAvailable { get; }

    /// <summary>The current live Deep-Slumber state, or null before the live container resolves.</summary>
    DeepSlumberState? GetState();

    /// <summary>Drives the game to become <paramref name="target"/>: enables the bound line/area(s)
    /// and reconciles their phantom factors to match, through the game's own <c>season_talent</c>
    /// dispatcher. Idempotent — a target already matching the live state applies nothing and returns
    /// <see cref="DeepSlumberApplyResult.AlreadyMatched"/>. Never bypasses game-side gating (combat,
    /// cost, unlock, missing factor item); refusals surface in the result and the game toasts the
    /// reason. Call on the game Update thread (or a plugin callback that runs there).</summary>
    /// <param name="target">The setup to apply, typically a snapshot from <see cref="GetState"/>.</param>
    /// <param name="ct">Cancels remaining operations before their dispatch.</param>
    /// <returns>The aggregate outcome.</returns>
    Task<DeepSlumberApplyResult> ApplySetupAsync(DeepSlumberSetup target, CancellationToken ct = default);
}
