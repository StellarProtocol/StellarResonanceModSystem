using System.Collections.Generic;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

public class SocialTeamReaderTests
{
    // varint tag = (field << 3) | wire; LEN wire=2, VARINT wire=0.
    private static void Tag(List<byte> b, int field, int wire) => Varint(b, (uint)((field << 3) | wire));
    private static void Varint(List<byte> b, ulong v) { while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; } b.Add((byte)v); }
    private static void Len(List<byte> b, int field, byte[] inner) { Tag(b, field, 2); Varint(b, (ulong)inner.Length); b.AddRange(inner); }
    private static void VInt(List<byte> b, int field, ulong v) { Tag(b, field, 0); Varint(b, v); }
    private static byte[] Str(string s) { var b = new List<byte>(); foreach (var c in s) b.Add((byte)c); return b.ToArray(); }

    // map entry { key = 1, value = 2 TeamMemData{ ..., social_data = 9 TeamMemberSocialData } }
    private static byte[] MemberEntry(long key, long charId, string name, int level, int prof, ulong fp, int online, int scene)
    {
        var basic = new List<byte>(); Len(basic, 3, Str(name)); VInt(basic, 6, (ulong)level);
        var profession = new List<byte>(); VInt(profession, 1, (ulong)prof);
        var attr = new List<byte>(); VInt(attr, 4, fp);
        var social = new List<byte>(); Len(social, 1, basic.ToArray()); Len(social, 4, profession.ToArray()); Len(social, 8, attr.ToArray());
        var mem = new List<byte>();
        if (charId != 0) VInt(mem, 1, (ulong)charId);
        VInt(mem, 2, 1_700_000_000); VInt(mem, 4, 3); VInt(mem, 5, (ulong)online); VInt(mem, 6, (ulong)scene);
        Len(mem, 9, social.ToArray());
        var entry = new List<byte>(); VInt(entry, 1, (ulong)key); Len(entry, 2, mem.ToArray());
        return entry.ToArray();
    }

    [Fact]
    public void Read_decodes_header_packed_charIds_and_members()
    {
        var t = new List<byte>();
        VInt(t, 1, 900); VInt(t, 2, 11); VInt(t, 3, 5005); VInt(t, 4, 2);
        { var packed = new List<byte>(); Varint(packed, 11); Varint(packed, 5_000_000_000); Len(t, 5, packed.ToArray()); }
        Len(t, 8, MemberEntry(11, 11, "Lead", 60, 7, 47597, 1, 8));
        Len(t, 8, MemberEntry(5_000_000_000, 0, "Far", 55, 3, 100, 0, 12023));   // char_id omitted → map key
        VInt(t, 9, 1);

        var p = SocialTeamReader.Read(t.ToArray());

        Assert.Equal(900, p.TeamId);
        Assert.Equal(11, p.LeaderId);
        Assert.Equal(5005, p.TeamTargetId);
        Assert.Equal(2, p.TeamNum);
        Assert.Equal(1, p.MemberType);
        Assert.Equal(new long[] { 11, 5_000_000_000 }, p.CharIds);
        Assert.Equal(2, p.Members.Count);
        Assert.Equal("Lead", p.Members[0].Name);
        Assert.Equal(60, p.Members[0].Level);
        Assert.Equal(7, p.Members[0].ProfessionId);
        Assert.Equal(47597, p.Members[0].FightPoint);
        Assert.Equal(3, p.Members[0].TalentId);
        Assert.Equal(1_700_000_000u, p.Members[0].EnterTime);
        Assert.Equal(5_000_000_000, p.Members[1].CharId);
        Assert.Equal(12023, p.Members[1].SceneId);
    }

    [Fact]
    public void Read_accepts_unpacked_charIds()
    {
        var t = new List<byte>(); VInt(t, 5, 21); VInt(t, 5, 22);
        Assert.Equal(new long[] { 21, 22 }, SocialTeamReader.Read(t.ToArray()).CharIds);
    }

    [Fact]
    public void Read_empty_section_yields_zeroed_party()
    {
        var p = SocialTeamReader.Read(System.Array.Empty<byte>());
        Assert.Equal(0, p.TeamId);
        Assert.Empty(p.CharIds);
        Assert.Empty(p.Members);
    }
}
