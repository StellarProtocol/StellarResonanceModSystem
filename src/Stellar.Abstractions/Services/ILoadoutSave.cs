using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;

namespace Stellar.Abstractions.Services;

/// <summary>Save the setup the player is WEARING into one of their saved in-game loadouts — the
/// game's own "Save" (its Role Plan <c>AsyncSaveRolePlan</c>), aimed at a loadout other than the worn
/// one. Split from <see cref="ILoadout"/> (read + switch) so that interface stays within its member
/// budget; reach it through <see cref="IPluginServices.LoadoutSave"/>.
///
/// <para><b>What a save copies.</b> The server stores the LIVE setup — class, gear, modules, skill bar
/// (including the Battle Imagine slots) and talents — into the target loadout, including any edits the
/// player has not saved to the worn loadout yet. The target keeps its own name. The player stays on the
/// worn loadout; nothing switches. Deep-Slumber is not part of a game loadout.</para>
///
/// <para><b>Validation.</b> The game's own wrapper runs the request, so the server validates it (for
/// example it refuses in combat) and the game shows its own success or refusal tip. This service never
/// bypasses that.</para></summary>
public interface ILoadoutSave
{
    /// <summary>Saves the worn setup into the loadout identified by <paramref name="index"/>.
    /// Completes without sending anything when <paramref name="index"/> is the worn loadout
    /// (<see cref="LoadoutResult.Rejected"/>), is not a saved loadout
    /// (<see cref="LoadoutResult.NoSuchLoadout"/>), when the worn loadout is not known yet
    /// (<see cref="LoadoutResult.GameApiUnavailable"/>), or while a loadout switch or another save is
    /// still in flight (<see cref="LoadoutResult.Rejected"/>). A refusal from the game itself is
    /// <see cref="LoadoutResult.Rejected"/>. On <see cref="LoadoutResult.Success"/> the framework
    /// re-reads the loadout list, so <see cref="ILoadout.GetSlots"/> and
    /// <see cref="ILoadout.LoadoutsChanged"/> catch up with the saved target shortly after.</summary>
    /// <param name="index">A <see cref="LoadoutSlot.Index"/> value other than the worn loadout's.</param>
    /// <param name="ct">Cancels the request before it is sent to the game. Once sent, the save cannot be
    /// recalled and the task completes with the game's answer.</param>
    /// <returns>The outcome of the save.</returns>
    Task<LoadoutResult> SaveCurrentToAsync(int index, CancellationToken ct = default);

    /// <summary>True when the worn setup differs from the worn loadout's saved data — the game's own
    /// "unsaved changes" check (the one behind its switch warning). Cached: re-evaluated when the game
    /// merges new character data, when the loadout list is re-read, and after a save — never on a timer.
    /// False until the first evaluation. Read on the game tick.</summary>
    bool HasUnsavedChanges { get; }
}
