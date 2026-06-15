using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Plugin-facing control over the local player's party. Actions are routed
/// through the game's own dispatcher (never hand-built packets); the game
/// applies its own validation, so a request may be silently rejected (e.g. a
/// non-leader, or the 20-player mode being locked for the account).
/// </summary>
public interface IPartyControl
{
    /// <summary>True once the game-side team bridge has resolved and a request can be issued.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Requests the game switch the current party between 5-player
    /// (<see cref="PartyType.Regular5"/>) and 20-player raid
    /// (<see cref="PartyType.Raid20"/>). Must be called on the main thread, from a
    /// user-initiated command. No-op for <see cref="PartyType.Solo"/>, when the
    /// requested size already matches, or when not the party leader. The party's
    /// activity target is preserved. The result is observed via
    /// <see cref="IPartySnapshot.PartyType"/> once the server broadcasts it.
    /// </summary>
    void SetMemberType(PartyType size);

    /// <summary>
    /// Moves <paramref name="charId"/> to raid <paramref name="group"/> (1–4) at
    /// <paramref name="slot"/> (0–4) via the game's own <c>AsyncUpdateTeamGroup</c>
    /// dispatcher — never a hand-built packet. Must be called on the main thread from a
    /// user-initiated command. Leader-only and 20-player-raid-only; the game validates
    /// and may silently reject. No-op when the team bridge is unresolved. The result is
    /// observed via the party roster's group/slot once the server broadcasts it.
    /// </summary>
    /// <param name="charId">Character id of the member to move.</param>
    /// <param name="group">Destination raid group, 1-based (1–4).</param>
    /// <param name="slot">Destination slot within the group, 0-based (0–4).</param>
    void MoveMember(long charId, int group, int slot);
}
