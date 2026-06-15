namespace Stellar.Abstractions.Domain;

/// <summary>
/// One party member's snapshot. Composed from wire deliveries on the
/// <c>GrpcTeamNtf</c> service. Immutable; rebuilt by <c>PartyService</c>
/// whenever any field changes.
/// </summary>
/// <remarks>
/// <para><b>CharId</b> — Wire <c>char_id</c>. Also <c>EntityId.Uid</c> under the Phase 4 identity assumption.</para>
/// <para><b>Name</b> — <c>TeamMemberSocialData.basic_data.name</c>. <c>null</c> until first social sync arrives.</para>
/// <para><b>Profession</b> — Raw profession id from <c>TeamMemberSocialData.profession_data</c>. Phase 5 IGameData will translate to a name.</para>
/// <para><b>Level</b> — Character level from <c>TeamMemberSocialData.basic_data</c>.</para>
/// <para><b>Hp</b> — Live HP from <c>TeamMemberFastSyncData</c>.</para>
/// <para><b>MaxHp</b> — Maximum HP from <c>TeamMemberFastSyncData</c>.</para>
/// <para><b>SceneId</b> — Scene id from fast-sync. When this differs from the local scene, the member is out-of-AOI but HP is still tracked via wire.</para>
/// <para><b>Position</b> — World-space position of the member.</para>
/// <para><b>IsOnline</b> — <c>TeamMemData.online_status == 1</c>.</para>
/// <para><b>IsSelf</b> — True when <c>CharId</c> matches the local player. False until <c>ICombatSnapshot.LocalEntityId</c> becomes non-None — known v1 quirk if joining a party before first combat.</para>
/// <para><b>GroupId</b> — <c>TeamMemData.group_id</c>. 0 in non-raid parties; meaningful only when <c>PartyType == Raid20</c>.</para>
/// <para><b>Slot</b> — 0-based position WITHIN the group (the member's order in the team's slot list), from
/// <c>NotifyTeamGroupUpdate</c> (<c>TeamMemberGroupInfo.char_ids</c> ordering). -1 when not yet known. Lets the
/// raid grid place each member at their exact Team×Slot instead of roster order.</para>
/// <para><b>DPS aggregation</b> — query <c>ICombatLookup.GetLiveDps(member.EntityId)</c> instead of a per-member DPS field. The combat service aggregates per source EntityId for every entity in AOI, so the same call works for party members, mobs, and randoms uniformly.</para>
/// </remarks>
public readonly record struct PartyMember(
    long       CharId,
    string?    Name,
    int        Profession,
    int        Level,
    long       Hp,
    long       MaxHp,
    int        SceneId,
    Position3D Position,
    bool       IsOnline,
    bool       IsSelf,
    int        GroupId,
    int        Slot = -1,
    int        Talent = 0)   // TeamMemData.talent_id — the member's selected talent/spec (0 = none)
{
    /// <summary>
    /// Combat entity id derived from <see cref="CharId"/> under the Phase 4 identity
    /// assumption (<c>CharId &lt;&lt; 16 | 640</c>). Use to cross-reference <c>ICombatEvents</c> events.
    /// </summary>
    public EntityId EntityId => new((CharId << 16) | 640);

    /// <summary>True when current HP fraction is below <paramref name="threshold"/> (default 30%).</summary>
    public bool IsLowHp(float threshold = 0.30f)
        => MaxHp > 0 && (float)Hp / MaxHp < threshold;
}
