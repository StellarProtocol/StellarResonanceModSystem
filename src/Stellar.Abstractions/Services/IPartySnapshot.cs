using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-only scalar state of the local player's party. All properties are
/// observed on the Unity main thread.
/// </summary>
public interface IPartySnapshot
{
    /// <summary>True once the first GrpcTeamNtf delivery has been seen (whether or not the player is in a party).</summary>
    bool IsAvailable { get; }

    /// <summary>True when in a party of 2+ members. Solo = false.</summary>
    bool IsInParty { get; }

    /// <summary>0 when solo.</summary>
    long PartyId { get; }

    /// <summary>0 when solo.</summary>
    long LeaderCharId { get; }

    /// <summary>Convenience: <see cref="LeaderCharId"/> matches the local player's char_id.</summary>
    bool IsLeader { get; }

    /// <summary>Party size category. <see cref="PartyType.Solo"/> when not in a party.</summary>
    PartyType PartyType { get; }

    /// <summary>True when the party is currently queued for matchmaking.</summary>
    bool IsMatching { get; }

    /// <summary>Convenience: the member with <c>IsSelf == true</c>, or null if not yet identified.</summary>
    PartyMember? Self { get; }
}
