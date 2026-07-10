using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Wire-shaped snapshot delivered by the probe. Not exposed to plugins.
/// <c>PartyService</c> diffs this against its current slot map.
/// </summary>
public sealed record PartyWireSnapshot(
    long              PartyId,
    long              LeaderCharId,
    PartyType         PartyType,
    bool              IsMatching,
    IReadOnlyList<PartyMemberRoster> Roster,
    IReadOnlyList<TeamGroupInfo>? Groups = null);   // raid group/slot layout from TeamBaseInfo.team_member_group_infos

/// <summary>One member's row inside a <see cref="PartyWireSnapshot"/>.</summary>
public sealed record PartyMemberRoster(
    long                    CharId,
    int                     EnterTimeRaw,
    int                     OnlineStatusRaw,
    int                     SceneId,
    int                     GroupId,
    PartyMemberFastSync?    FastSync,
    PartyMemberSocialSync?  Social,
    int                     TalentId = 0,    // TeamMemData.talent_id — selected talent/spec
    bool?                   VoiceOpen = null,  // TeamMemData.voice_is_open (coarse open/closed)
    int?                    MicStatusRaw = null, // TeamMemRealTimeVoiceInfo.microphone_status (full EMicrophoneStatus)
    bool?                   Speaking = null);  // TeamMemRealTimeVoiceInfo.speak_status == Begin

/// <summary>Per-member live data from <c>TeamMemberFastSyncData</c>.</summary>
public sealed record PartyMemberFastSync(
    int        SceneId,
    Position3D Position,
    long       Hp,
    long       MaxHp,
    int        StateRaw);

/// <summary>Per-member slower-moving data from <c>TeamMemberSocialData</c>.</summary>
public sealed record PartyMemberSocialSync(
    string? Name,
    int     Level,
    int     Profession,
    int     GroupId,
    string  ProfileUrl = "",
    string  HalfBodyUrl = "");
