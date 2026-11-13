using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

// basic_data presence trio: personal_state = 8 (repeated int32), offline_time = 9, online_time = 16.
public class SocialDataReaderPresenceTests
{
    private static void Tag(List<byte> b, int field, int wire) => Varint(b, (uint)((field << 3) | wire));
    private static void Varint(List<byte> b, ulong v) { while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; } b.Add((byte)v); }
    private static void Len(List<byte> b, int field, byte[] inner) { Tag(b, field, 2); Varint(b, (ulong)inner.Length); b.AddRange(inner); }
    private static void VInt(List<byte> b, int field, ulong v) { Tag(b, field, 0); Varint(b, v); }
    private static byte[] Str(string s) { var b = new List<byte>(); foreach (var c in s) b.Add((byte)c); return b.ToArray(); }

    private static SocialSnapshot? Decode(System.Action<List<byte>> basicExtras, System.Action<List<byte>>? dataExtras = null)
    {
        var basic = new List<byte>(); Len(basic, 3, Str("Eiori")); VInt(basic, 6, 60); basicExtras(basic);
        var data = new List<byte>(); VInt(data, 1, 4242); Len(data, 3, basic.ToArray()); dataExtras?.Invoke(data);
        var reply = new List<byte>(); Len(reply, 2, data.ToArray());
        return SocialDataReader.Read(reply.ToArray());
    }

    [Fact]
    public void Online_player_has_no_offline_time_and_reads_online()
    {
        // proto3 omits zero: an online player's basic_data simply has no field 9.
        var snap = Decode(b => VInt(b, 16, 1_763_000_000_000UL))!;
        Assert.True(snap.IsOnline);
        Assert.Equal(0, snap.Presence!.Value.OfflineTimeMs);
        Assert.Equal(1_763_000_000_000L, snap.Presence!.Value.OnlineTimeMs);
        Assert.Empty(snap.Presence!.Value.PersonalStates);
        Assert.Equal("Eiori", snap.Name);                      // name/level still decoded in the same walk
        Assert.Equal(60, snap.Level);
    }

    [Fact]
    public void Offline_player_reports_offline_time_in_ms()
    {
        var snap = Decode(b => { VInt(b, 9, 1_763_012_345_678UL); VInt(b, 16, 1_763_000_000_000UL); })!;
        Assert.False(snap.IsOnline);
        Assert.Equal(1_763_012_345_678L, snap.Presence!.Value.OfflineTimeMs);
    }

    [Fact]
    public void Packed_personal_state_decodes_in_order()
    {
        var snap = Decode(b => { var p = new List<byte>(); Varint(p, 1); Varint(p, 2); Varint(p, 300); Len(b, 8, p.ToArray()); })!;
        Assert.Equal(new[] { 1, 2, 300 }, snap.Presence!.Value.PersonalStates);
    }

    [Fact]
    public void Unpacked_personal_state_decodes_in_order()
    {
        var snap = Decode(b => { VInt(b, 8, 2); VInt(b, 8, 1); })!;
        Assert.Equal(new[] { 2, 1 }, snap.Presence!.Value.PersonalStates);
    }

    [Fact]
    public void Negative_personal_state_survives_sign_extension()
    {
        var snap = Decode(b => VInt(b, 8, unchecked((ulong)(long)-1)))!;
        Assert.Equal(new[] { -1 }, snap.Presence!.Value.PersonalStates);
    }

    [Fact]
    public void Snapshot_without_basic_data_has_unknown_online_state()
    {
        // The wire reader rejects a reply without basic_data (no name → null); a hand-built snapshot has no presence.
        var reply = new List<byte>(); { var d = new List<byte>(); VInt(d, 1, 4242); Len(reply, 2, d.ToArray()); }
        Assert.Null(SocialDataReader.Read(reply.ToArray()));

        var built = new SocialSnapshot(4242, "Eiori", 60, 0, 0, System.Array.Empty<GearSlotRef>(),
            System.Array.Empty<Stellar.Abstractions.Domain.FashionEntry>(), SocialIdentity.None);
        Assert.Null(built.Presence);
        Assert.Null(built.IsOnline);
        Assert.Equal(SocialSections.None, built.Sections);
    }

    [Fact]
    public void Default_presence_reports_empty_states_not_null()
        => Assert.Empty(default(SocialPresence).PersonalStates);

    [Fact]
    public void Sections_record_exactly_what_the_reply_carried()
    {
        var thin = Decode(_ => { }, d => { var z = new List<byte>(); Len(z, 11, new byte[] { 0x08, 0x01 }); Len(d, 16, z.ToArray()); })!;
        Assert.Equal(SocialSections.Basic | SocialSections.PersonalZone, thin.Sections);

        var full = Decode(_ => { }, d =>
        {
            Len(d, 6, new byte[] { 0x08, 0x07 });    // profession_data
            Len(d, 7, System.Array.Empty<byte>());   // equip_data present but empty
            Len(d, 11, new byte[] { 0x20, 0x05 });   // user_attr_data.fight_point
            Len(d, 10, System.Array.Empty<byte>());  // scene_data
        })!;
        Assert.Equal(SocialSections.Basic | SocialSections.Profession | SocialSections.Equip | SocialSections.Attr
                     | SocialSections.Scene, full.Sections);
    }
}
