using System.Linq;
using Stellar.Infrastructure.Game.Protobuf;
using Stellar.Application.Tests.Wire;
using Xunit;

namespace Stellar.Application.Tests.Party.Protobuf;

public sealed class GetTeamInfoReplyReaderTests
{
    private static byte[] Member(long charId, string name, int level)
    {
        var basic  = new WireBytes().Tag(3, 2).String(name).Tag(6, 0).Varint((ulong)level).ToArray();   // BasicData
        var social = new WireBytes().Tag(1, 2).LengthDelimited(basic).ToArray();                          // TeamMemberSocialData.basic@1
        return new WireBytes()
            .Tag(1, 0).Varint((ulong)charId)    // TeamMemData.charId@1
            .Tag(9, 2).LengthDelimited(social)   // TeamMemData.social@9
            .ToArray();
    }

    private static byte[] Reply(long teamId, long leader, params byte[][] members)
    {
        var baseInfo = new WireBytes()
            .Tag(1, 0).Varint((ulong)teamId)
            .Tag(3, 0).Varint((ulong)leader)
            .ToArray();                                                   // TeamBaseInfo
        var wb = new WireBytes().Tag(1, 2).LengthDelimited(baseInfo);    // GetTeamInfoReply.base_info@1
        foreach (var m in members) wb.Tag(2, 2).LengthDelimited(m);     // repeated member_data@2
        return wb.ToArray();
    }

    private static byte[] Wrapped(byte[] reply) =>
        new WireBytes().Tag(1, 2).LengthDelimited(reply).ToArray();      // GetTeamInfo_Ret { ret@1 }

    [Fact]
    public void Wrapped_TwoMembers_ParsesRosterWithNames()
    {
        var reply = Reply(177792287, 63696951,
            Member(63696951, "Ribery",  60),
            Member(1248014,  "Revette", 60));
        var payload = Wrapped(reply);

        Assert.True(GetTeamInfoReplyReader.TryRead(payload, out var snap));
        Assert.Equal(177792287L, snap.PartyId);
        Assert.Equal(63696951L,  snap.LeaderCharId);
        Assert.Equal(2, snap.Roster.Count);

        var ribery = snap.Roster.Single(m => m.CharId == 63696951L);
        Assert.Equal("Ribery", ribery.Social!.Name);
        Assert.Equal(60,       ribery.Social!.Level);
        Assert.Contains(snap.Roster, m => m.Social?.Name == "Revette");
    }

    [Fact]
    public void Unwrapped_DirectReply_AlsoParses()   // defensive: no _Ret wrapper
    {
        var reply = Reply(5, 9, Member(9, "Solo", 60));
        Assert.True(GetTeamInfoReplyReader.TryRead(reply, out var snap));
        Assert.Single(snap.Roster);
    }

    [Fact]
    public void SocialFrame_NotTeamInfo_Rejected()   // inner f1 is a varint, not a TeamBaseInfo message
    {
        // payload.f1 = { f1=varint(540678), f2=member } — a friend/social profile shape
        var member = Member(20854, "NekoChan", 60);
        var inner  = new WireBytes()
            .Tag(1, 0).Varint(540678)
            .Tag(2, 2).LengthDelimited(member)
            .ToArray();
        var payload = new WireBytes().Tag(1, 2).LengthDelimited(inner).ToArray();

        Assert.False(GetTeamInfoReplyReader.TryRead(payload, out _));
    }

    [Fact]
    public void BaseInfoOnly_NoMembers_Rejected()
    {
        var reply = Reply(5, 9);   // base_info, zero members
        Assert.False(GetTeamInfoReplyReader.TryRead(Wrapped(reply), out _));
    }

    [Fact]
    public void Return_FirstTagNotField1Message_RejectedFast()
    {
        // A Return that leads with a varint field 1 (e.g. a typical non-team RPC reply)
        var payload = new WireBytes().Tag(1,0).Varint(12345).Tag(2,2).String("x").ToArray();
        Assert.False(GetTeamInfoReplyReader.TryRead(payload, out _));
    }
}
