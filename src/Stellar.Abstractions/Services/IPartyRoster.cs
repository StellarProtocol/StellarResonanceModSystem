using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-only access to the current party roster. Observed on the Unity main thread.
/// </summary>
public interface IPartyRoster
{
    /// <summary>
    /// All party members including self. Sorted with self first, then by join order.
    /// Empty when solo. The reference is stable for the frame; the snapshot is rebuilt
    /// lazily when state changes.
    /// </summary>
    IReadOnlyList<PartyMember> Members { get; }

    /// <summary>The member's team-voice microphone mode (<c>voice_is_open</c> base, refined by
    /// GrpcTeamNtf method 25). Returns <see cref="MicrophoneStatus.Opened"/> for an unknown member.</summary>
    MicrophoneStatus GetMicStatus(long charId);

    /// <summary>Whether the member is currently talking (GrpcTeamNtf method 26). <c>false</c> for an unknown member.</summary>
    bool IsSpeaking(long charId);
}
