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

    /// <summary>
    /// Transfer party leadership to <paramref name="charId"/> via the game's own
    /// <c>AsyncTransferLeader</c> dispatcher. Leader-only; validated game-side. No-op
    /// when not leader or when the team bridge is unresolved.
    /// </summary>
    void TransferLeader(long charId);

    /// <summary>
    /// Kick <paramref name="charId"/> from the party via the game's own
    /// <c>AsyncTickOut</c> dispatcher. Leader-only; validated game-side. No-op
    /// when not leader or when the team bridge is unresolved.
    /// </summary>
    void KickMember(long charId);

    /// <summary>
    /// Invite <paramref name="charId"/> to the party via the game's own
    /// <c>AsyncInviteToTeam</c> dispatcher. Any member may invite; the game
    /// enforces party-full and cooldown constraints. No-op when the team bridge
    /// is unresolved.
    /// </summary>
    void InviteToTeam(long charId);

    /// <summary>
    /// Leave the current party via the game's own <c>AsyncQuitTeam</c> dispatcher.
    /// No-op when the team bridge is unresolved. The game handles server notification
    /// and clears team state.
    /// </summary>
    void LeaveParty();
}
