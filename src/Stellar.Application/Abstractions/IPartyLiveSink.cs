namespace Stellar.Application.Abstractions;

/// <summary>
/// Probe-facing write surface for the party's transient/live status streams
/// (ready-check votes and team-voice mic/speak), kept separate from the
/// roster-mutating <see cref="IPartyEventSink"/>. Implemented by
/// <c>PartyService</c>; all methods are fire-and-forget and thread-safe (queued
/// and drained on the Unity main thread).
/// </summary>
public interface IPartyLiveSink
{
    /// <summary>A member responded to a ready-check (<c>WorldNtf.NotifyCaptainReady</c>, method 71).</summary>
    void EnqueueReadyCheckResponse(long charId, string? name, bool isReady);

    /// <summary>Ready-check window opened (<c>true</c>) or closed (<c>false</c>)
    /// (<c>WorldNtf.NotifyAllMemberReady</c>, method 70; non-leader clients only).</summary>
    void EnqueueReadyCheckPhase(bool isOpen);

    /// <summary>A member's microphone mode changed (<c>GrpcTeamNtf</c> method 25). <paramref name="micStatusRaw"/>
    /// is the raw <c>EMicrophoneStatus</c>.</summary>
    void EnqueueMicStatus(long charId, int micStatusRaw);

    /// <summary>A member's speaking status changed (<c>GrpcTeamNtf</c> method 26, one call per map entry).
    /// <paramref name="speakStatusRaw"/> is the raw <c>ESpeakStatus</c>.</summary>
    void EnqueueSpeakStatus(long charId, int speakStatusRaw);
}
