namespace Stellar.Wire;

/// <summary>
/// Method IDs for the <c>GrpcTeamNtf</c> service (UUID 966773353). Method
/// numbers correspond to the field order of the <c>GrpcTeamNtf</c> oneof in
/// <c>(local reference)</c>. Verified
/// against observed wire traffic during recon: method=2 fast-sync delivery,
/// method=26/27 voice-chat status, match the corresponding proto positions.
/// </summary>
public static class GrpcTeamNtfMethodIds
{
    public const uint NoticeUpdateTeamInfo        = 1;
    public const uint NoticeUpdateTeamMemberInfo  = 2;
    public const uint NotifyJoinTeam              = 3;
    public const uint NotifyLeaveTeam             = 4;
    public const uint NoticeTeamDissolve          = 13;
    /// <summary>Raid group/slot layout — sent when a member's team or in-team position changes (drag in the
    /// raid-position editor). Carries map&lt;int32, TeamMemberGroupInfo{group_id, repeated char_ids}&gt;; the
    /// char_ids order is the per-group slot order. (Confirmed from a live SEND World→RECV GrpcTeamNtf m29.)</summary>
    public const uint NotifyTeamGroupUpdate       = 29;
}
