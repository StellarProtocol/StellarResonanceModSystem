using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Party membership lifecycle events. All events fire on the Unity main thread.
/// </summary>
public interface IPartyEvents
{
    /// <summary>Fires when a new member appears in the roster.</summary>
    event Action<PartyMember> MemberJoined;

    /// <summary>
    /// Fires when a member leaves. The <see cref="PartyMember"/> argument carries
    /// last-known state at the time of leave.
    /// </summary>
    event Action<PartyMember, PartyLeaveKind> MemberLeft;

    /// <summary>
    /// Fires when any field of a member changes (HP, scene, online, profession, group, etc.).
    /// Multiple field changes from one wire delivery are coalesced into a single event.
    /// </summary>
    event Action<PartyMember> MemberUpdated;

    /// <summary>Fires when the party disbands. After firing, the roster is empty.</summary>
    event Action PartyDissolved;

    /// <summary>
    /// Fires when a member responds to a dungeon ready-check
    /// (<c>WorldNtf.NotifyCaptainReady</c>, method 71) — carries who responded and
    /// whether they readied or declined. Every client in the party receives this,
    /// including the leader. Use it to drive a live ready-check panel.
    /// </summary>
    event Action<ReadyCheckResponse> ReadyCheckResponded;

    /// <summary>
    /// Fires when the ready-check window opens (<c>true</c>) or closes (<c>false</c>)
    /// via <c>WorldNtf.NotifyAllMemberReady</c> (method 70). NOTE: the party LEADER
    /// who initiates the check does NOT receive this packet — only non-leader members
    /// do. Leaders should track open/close from their own initiation + the prepare
    /// window timer.
    /// </summary>
    event Action<bool> ReadyCheckPhaseChanged;
}
