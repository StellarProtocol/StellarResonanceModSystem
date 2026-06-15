using Stellar.Application.Services;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Sink methods the probe calls (from IL2CPP stub thread or wire-tap thread)
/// to enqueue deltas. Implemented by <c>PartyService</c>; payloads are queued
/// and drained on the Unity main thread.
///
/// <para>All methods are fire-and-forget and must be safe to call from any
/// thread. The implementation uses <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>
/// internally.</para>
/// </summary>
public interface IPartyEventSink
{
    /// <summary>
    /// Roster delivery. <paramref name="authoritative"/> = the snapshot is the COMPLETE roster and may
    /// prune members absent from it (only the explicit full fetch — <c>GetTeamInfoReply</c>/<c>CreateTeam_Ret</c>).
    /// Push notifications (<c>NotifyJoinTeam</c>, <c>NoticeUpdateTeamInfo</c>) are <b>additive</b>
    /// (<c>authoritative: false</c>): they carry only a subset (e.g. just the joiner), so pruning against them
    /// would wipe the rest of the party. Departures arrive via <see cref="EnqueueMemberLeft"/>/<see cref="EnqueueDissolve"/>.
    /// </summary>
    void EnqueueFullSnapshot(PartyWireSnapshot snapshot, bool authoritative = false);

    /// <summary>Per-member fast sync (HP, position, scene).</summary>
    void EnqueueMemberFastSync(long charId, PartyMemberFastSync data);

    /// <summary>Per-member social data (name, level, profession, online, group).</summary>
    void EnqueueMemberSocialSync(long charId, PartyMemberSocialSync data);

    /// <summary>One member left the party. <paramref name="leaveTypeRaw"/> is the raw wire value.</summary>
    void EnqueueMemberLeft(long charId, int leaveTypeRaw);

    /// <summary>Raid group/slot layout (<c>NotifyTeamGroupUpdate</c>): assigns each member their group +
    /// in-group slot (the char-id order within a group is the slot order).</summary>
    void EnqueueGroupLayout(System.Collections.Generic.IReadOnlyList<TeamGroupInfo> groups);

    /// <summary>Party disbanded.</summary>
    void EnqueueDissolve();
}
