using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

public class SocialDataReaderTests
{
    // varint tag = (field << 3) | wire; LEN wire=2, VARINT wire=0.
    private static void Tag(List<byte> b, int field, int wire) => Varint(b, (uint)((field << 3) | wire));
    private static void Varint(List<byte> b, ulong v) { while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; } b.Add((byte)v); }
    private static void Len(List<byte> b, int field, byte[] inner) { Tag(b, field, 2); Varint(b, (ulong)inner.Length); b.AddRange(inner); }
    private static void VInt(List<byte> b, int field, ulong v) { Tag(b, field, 0); Varint(b, v); }
    private static byte[] Str(string s) { var b = new List<byte>(); foreach (var c in s) b.Add((byte)c); return b.ToArray(); }

    private static byte[] BasicData(string name, int level)
    { var b = new List<byte>(); Len(b, 3, Str(name)); VInt(b, 6, (ulong)level); return b.ToArray(); }
    private static byte[] EquipNine(int slot, int id)
    { var b = new List<byte>(); VInt(b, 1, (ulong)slot); VInt(b, 2, (ulong)id); return b.ToArray(); }

    [Fact]
    public void Read_decodes_identity_profession_fightpoint_and_gear()
    {
        var data = new List<byte>();
        VInt(data, 1, 4242);                                   // char_id
        Len(data, 3, BasicData("Eiori", 60));                  // basic_data
        { var p = new List<byte>(); VInt(p, 1, 7); Len(data, 6, p.ToArray()); }   // profession_data{profession_id=7}
        { var e = new List<byte>(); Len(e, 1, EquipNine(200, 1001)); Len(e, 1, EquipNine(205, 1002)); Len(data, 7, e.ToArray()); } // equip_data
        { var a = new List<byte>(); VInt(a, 4, 47597); Len(data, 11, a.ToArray()); } // user_attr_data{fight_point}
        { var t = new List<byte>(); VInt(t, 4, 5); Len(data, 12, t.ToArray()); }    // team_data{team_num=5}
        { var u = new List<byte>(); Len(u, 2, Str("Eroge")); Len(data, 13, u.ToArray()); } // union_data{name}
        { var z = new List<byte>(); VInt(z, 11, 301); Len(data, 16, z.ToArray()); } // personal_zone{title_id}
        { var m = new List<byte>(); VInt(m, 1, 2868); Len(data, 22, m.ToArray()); } // master_mode_dungeon_data{season_score}

        var reply = new List<byte>(); Len(reply, 2, data.ToArray());   // GetSocialDataReply.data = field 2

        var snap = SocialDataReader.Read(reply.ToArray());

        Assert.NotNull(snap);
        Assert.Equal(4242, snap!.CharId);
        Assert.Equal("Eiori", snap.Name);
        Assert.Equal(60, snap.Level);
        Assert.Equal(7, snap.ProfessionId);
        Assert.Equal(47597, snap.FightPoint);
        Assert.Equal(2, snap.Gear.Count);
        Assert.Equal(new GearSlotRef(200, 1001), snap.Gear[0]);
        Assert.Equal(new GearSlotRef(205, 1002), snap.Gear[1]);
        Assert.Equal(new SocialIdentity("Eroge", 5, 2868, 301), snap.Identity);
    }

    [Fact]
    public void Read_defaults_identity_when_sections_absent()
    {
        // Thin-mask replies (nameplate/avatar queries) omit team/union/master sections entirely.
        var data = new List<byte>();
        VInt(data, 1, 4242);
        Len(data, 3, BasicData("Eiori", 60));
        var reply = new List<byte>(); Len(reply, 2, data.ToArray());

        var snap = SocialDataReader.Read(reply.ToArray());

        Assert.NotNull(snap);
        Assert.Equal(SocialIdentity.None, snap!.Identity);
    }

    [Fact]
    public void Read_hides_master_score_when_is_show_flag_set()
    {
        // master_mode_dungeon_data.is_show is INVERTED: truthy renders "Hidden" on the native card.
        var data = new List<byte>();
        VInt(data, 1, 4242);
        Len(data, 3, BasicData("Eiori", 60));
        { var m = new List<byte>(); VInt(m, 1, 2868); VInt(m, 2, 1); Len(data, 22, m.ToArray()); }
        var reply = new List<byte>(); Len(reply, 2, data.ToArray());

        var snap = SocialDataReader.Read(reply.ToArray());

        Assert.NotNull(snap);
        Assert.Equal(0, snap!.Identity.MasterScore);
    }

    [Fact]
    public void Read_preserves_large_int64_fightpoint()
    {
        // fight_point is int64 on the wire; values beyond int.MaxValue must not truncate.
        const long bigFightPoint = 5_000_000_000L;
        var data = new List<byte>();
        VInt(data, 1, 4242);
        Len(data, 3, BasicData("Eiori", 60));
        { var a = new List<byte>(); VInt(a, 4, (ulong)bigFightPoint); Len(data, 11, a.ToArray()); }
        var reply = new List<byte>(); Len(reply, 2, data.ToArray());

        var snap = SocialDataReader.Read(reply.ToArray());

        Assert.NotNull(snap);
        Assert.Equal(bigFightPoint, snap!.FightPoint);
    }

    [Fact]
    public void Read_returns_null_on_empty_or_garbage()
    {
        Assert.Null(SocialDataReader.Read(System.Array.Empty<byte>()));
        Assert.Null(SocialDataReader.Read(new byte[] { 0xFF, 0xFF, 0xFF }));
    }
}
