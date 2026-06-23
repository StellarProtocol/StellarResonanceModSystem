namespace Stellar.Abstractions.Domain;

/// <summary>
/// A party member's microphone mode, from the team voice system
/// (<c>EMicrophoneStatus</c> on the wire). Queried via
/// <c>IPartyRoster.GetMicStatus(charId)</c>.
/// </summary>
public enum MicrophoneStatus
{
    /// <summary>Mic on, listening (wire 0).</summary>
    Opened = 0,
    /// <summary>Speak mode (wire 1).</summary>
    Closed = 1,
    /// <summary>Speaker muted (wire 2).</summary>
    OpenSpeaker = 2,
}
