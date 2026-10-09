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
        { var z = new List<byte>(); VInt(z, 11, 301); VInt(z, 13, 9265); VInt(z, 18, 42); VInt(z, 20, 7); Len(data, 16, z.ToArray()); } // personal_zone{title_id, fashion_collect, ride_collect, weaponSkin_collect}
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
        Assert.Equal(new SocialIdentity("Eroge", 5, 2868, 301, 9265, 42, 7), snap.Identity);
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
        Assert.Equal(0, snap.Identity.FashionCollect);
        Assert.Equal(0, snap.Identity.RideCollect);
        Assert.Equal(0, snap.Identity.WeaponSkinCollect);
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

    [Fact]
    public void Read_decodes_avatar_picture_urls()
    {
        // SocialData.avatar_info = field 4; AvatarInfo{ profile=2 PictureInfo, half_body=3 PictureInfo }; PictureInfo.url = 1.
        var data = new List<byte>();
        VInt(data, 1, 4242);
        Len(data, 3, BasicData("Eiori", 60));
        var prof = new List<byte>(); Len(prof, 1, Str("https://cos.example/p.jpg"));
        var half = new List<byte>(); Len(half, 1, Str("https://cos.example/h.jpg"));
        var av = new List<byte>(); VInt(av, 1, 9); Len(av, 2, prof.ToArray()); Len(av, 3, half.ToArray());
        Len(data, 4, av.ToArray());
        var reply = new List<byte>(); Len(reply, 2, data.ToArray());

        var snap = SocialDataReader.Read(reply.ToArray());

        Assert.NotNull(snap);
        Assert.Equal("https://cos.example/p.jpg", snap!.ProfileUrl);
        Assert.Equal("https://cos.example/h.jpg", snap.HalfBodyUrl);
    }

    [Fact]
    public void Read_defaults_avatar_urls_empty_when_section_absent()
    {
        var data = new List<byte>();
        VInt(data, 1, 4242);
        Len(data, 3, BasicData("Eiori", 60));
        var reply = new List<byte>(); Len(reply, 2, data.ToArray());

        var snap = SocialDataReader.Read(reply.ToArray());

        Assert.NotNull(snap);
        Assert.Equal("", snap!.ProfileUrl);
        Assert.Equal("", snap.HalfBodyUrl);
    }

    // Position floats are wire type 5 (fixed32, little-endian IEEE-754).
    private static void F32(List<byte> b, int field, float v)
    { Tag(b, field, 5); b.AddRange(System.BitConverter.GetBytes(v)); }
    private static byte[] Position(float x, float y, float z, float dir)
    { var b = new List<byte>(); F32(b, 1, x); F32(b, 2, y); F32(b, 3, z); F32(b, 4, dir); return b.ToArray(); }

    [Fact]
    public void Read_decodes_scene_data_location()
    {
        var data = new List<byte>();
        VInt(data, 1, 4242);
        Len(data, 3, BasicData("Eiori", 60));
        {
            var s = new List<byte>();
            VInt(s, 1, 10010);                                  // map_id
            VInt(s, 2, 3);                                      // channel_id (ignored)
            Len(s, 3, Position(123.5f, -4.25f, 987.75f, 1.5f)); // pos
            VInt(s, 4, 77777777777);                            // level_uuid (int64, skipped)
            Len(s, 5, Position(1f, 2f, 3f, 0f));                // level_pos
            VInt(s, 6, 20020);                                  // level_map_id
            VInt(s, 10, 2);                                     // scene_layer
            VInt(s, 15, 9);                                     // line_id
            VInt(s, 18, unchecked((ulong)(long)-5));            // scene_area_id (negative int32)
            VInt(s, 19, 80006);                                 // level_area_id
            Len(data, 10, s.ToArray());
        }
        var reply = new List<byte>(); Len(reply, 2, data.ToArray());

        var snap = SocialDataReader.Read(reply.ToArray());

        Assert.NotNull(snap);
        Assert.NotNull(snap!.Location);
        var loc = snap.Location!.Value;
        Assert.Equal(10010, loc.MapId);
        Assert.Equal(2, loc.SceneLayer);
        Assert.Equal(9, loc.LineId);
        Assert.Equal(123.5f, loc.Pos.X);
        Assert.Equal(-4.25f, loc.Pos.Y);
        Assert.Equal(987.75f, loc.Pos.Z);
        Assert.Equal(1.5f, loc.Dir);
        Assert.Equal(-5, loc.SceneAreaId);
        Assert.Equal(20020, loc.LevelMapId);
        Assert.Equal(1f, loc.LevelPos.X);
        Assert.Equal(2f, loc.LevelPos.Y);
        Assert.Equal(3f, loc.LevelPos.Z);
        Assert.Equal(80006, loc.LevelAreaId);
        Assert.Equal(0L, loc.ReceivedAtMs);                     // the wire has no clock — the cache stamps it
        // Current* prefer the level fields (observed live) over the stale map/pos pair.
        Assert.Equal(20020, loc.CurrentSceneId);
        Assert.Equal(1f, loc.CurrentPos.X);
        Assert.Equal(80006, loc.CurrentAreaId);
    }

    [Fact]
    public void Read_location_is_zeroed_not_null_when_scene_data_present_but_empty()
    {
        // Present-but-blank (privacy) must stay distinguishable from absent — that's what the probe measures.
        var data = new List<byte>();
        VInt(data, 1, 4242);
        Len(data, 3, BasicData("Eiori", 60));
        Len(data, 10, System.Array.Empty<byte>());
        var reply = new List<byte>(); Len(reply, 2, data.ToArray());

        var snap = SocialDataReader.Read(reply.ToArray());

        Assert.NotNull(snap!.Location);
        Assert.Equal(default(SocialLocation), snap.Location!.Value);
    }

    [Fact]
    public void Read_location_null_when_scene_data_absent()
    {
        var data = new List<byte>();
        VInt(data, 1, 4242);
        Len(data, 3, BasicData("Eiori", 60));
        var reply = new List<byte>(); Len(reply, 2, data.ToArray());

        var snap = SocialDataReader.Read(reply.ToArray());

        Assert.NotNull(snap);
        Assert.Null(snap!.Location);
    }
}
