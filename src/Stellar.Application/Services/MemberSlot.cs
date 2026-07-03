using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Internal mutable state for one party member. Mirrored to the public
/// <see cref="PartyMember"/> record struct on each rebuild.
/// </summary>
internal sealed class MemberSlot
{
    public long       CharId;
    public string?    Name;
    public int        Profession;
    public int        Level;
    public long       Hp;
    public long       MaxHp;
    public int        SceneId;
    public Position3D Position;
    public int        OnlineStatusRaw;
    public int        EnterTimeRaw;
    public int        GroupId;
    public int        Slot = -1;   // 0-based position within the group (NotifyTeamGroupUpdate); -1 = unknown
    public int        TalentId;    // TeamMemData.talent_id — selected talent/spec
    public MicrophoneStatus MicStatus = MicrophoneStatus.Opened; // base from TeamMemData.voice_is_open, refined by GrpcTeamNtf m25
    public bool       Speaking;    // GrpcTeamNtf m26 (ESpeakStatus == Begin)
    public string     ProfileUrl = "";
    public string     HalfBodyUrl = "";

    public bool IsOnline => OnlineStatusRaw == 1;
}
