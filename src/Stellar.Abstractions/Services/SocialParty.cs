using System.Collections.Generic;

namespace Stellar.Abstractions.Services;

/// <summary>A player's party as carried by <c>SocialData.team_data</c> (field 12, <c>zproto.CharTeam</c>).
/// Every scalar defaults to 0 when its wire field is absent; the lists are never null.
/// <para>UNVERIFIED (2026-11-03): whether the server populates <paramref name="CharIds"/> /
/// <paramref name="Members"/> for players who are NOT in our own party, or only sends
/// <c>team_num</c> — the <c>[TeamDiag]</c> log line exists to find out.</para></summary>
/// <param name="TeamId">Party id from <c>team_data.team_id</c>; 0 when solo or blanked.</param>
/// <param name="LeaderId">Leader's char id from <c>team_data.leader_id</c>.</param>
/// <param name="TeamTargetId">Party target (activity/dungeon goal) id from <c>team_data.team_target_id</c>.</param>
/// <param name="TeamNum">Member count from <c>team_data.team_num</c> (same value as <see cref="SocialIdentity.PartySize"/>).</param>
/// <param name="MemberType">Party size class from <c>team_data.team_member_type</c> (<c>ETeamMemberType</c>;
/// the game's ID card treats <c>Five</c> as a 5-cap party and anything else as a 20-cap raid).</param>
/// <param name="CharIds">Member char ids from <c>team_data.char_ids</c>; empty when absent.</param>
/// <param name="Members">Per-member detail from the <c>team_data.team_member_data</c> map; empty when absent.</param>
public sealed record SocialParty(
    long TeamId,
    long LeaderId,
    int TeamTargetId,
    int TeamNum,
    int MemberType,
    IReadOnlyList<long> CharIds,
    IReadOnlyList<SocialPartyMember> Members);

/// <summary>One party member from <c>team_data.team_member_data</c> (<c>zproto.TeamMemData</c> + its
/// <c>TeamMemberSocialData</c>). Fields default to 0/empty when absent on the wire.</summary>
/// <param name="CharId">Member char id (<c>TeamMemData.char_id</c>, falling back to the map key).</param>
/// <param name="Name">Display name from <c>social_data.basic_data.name</c>.</param>
/// <param name="Level">Level from <c>social_data.basic_data.level</c>.</param>
/// <param name="ProfessionId">Profession id from <c>social_data.profession_data.profession_id</c>.</param>
/// <param name="TalentId">Talent id from <c>TeamMemData.talent_id</c>.</param>
/// <param name="FightPoint">Ability score from <c>social_data.user_attr_data.fight_point</c>.</param>
/// <param name="OnlineStatus">Online status from <c>TeamMemData.online_status</c> (raw enum value).</param>
/// <param name="SceneId">Scene id from <c>TeamMemData.scene_id</c>.</param>
/// <param name="EnterTime">Join time from <c>TeamMemData.enter_time</c> (raw; unit unconfirmed — likely Unix seconds).</param>
public readonly record struct SocialPartyMember(
    long CharId, string Name, int Level, int ProfessionId, int TalentId,
    long FightPoint, int OnlineStatus, int SceneId, uint EnterTime);
