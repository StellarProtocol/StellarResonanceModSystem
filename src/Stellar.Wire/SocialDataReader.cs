using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Wire;

/// <summary>Pure parser for the game's <c>Social.GetSocialData</c> reply
/// (<c>GetSocialDataReply{ mask=1, data=2 SocialData, err_code=3 }</c>) into a <see cref="SocialSnapshot"/>.
/// Defensive: malformed input yields what parsed so far (or null when nothing usable), never throws.</summary>
public static class SocialDataReader
{
    /// <summary>Decode a GetSocialDataReply payload. Null if no SocialData / char_id parsed.</summary>
    public static SocialSnapshot? Read(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (field == 2 && wire == 2 && WireProtocol.TryReadLengthDelimited(payload, ref pos, out var data))
                return ReadSocialData(data);
            if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        return null;
    }

    private static SocialSnapshot? ReadSocialData(ReadOnlySpan<byte> payload)
    {
        long charId = 0, fightPoint = 0; int level = 0, professionId = 0; string name = "", guild = "";
        int partySize = 0, masterScore = 0, titleId = 0;
        int fashionCollect = 0, rideCollect = 0, weaponSkinCollect = 0;
        string profileUrl = "", halfBodyUrl = "";
        var gear = new List<GearSlotRef>(11);
        IReadOnlyList<FashionEntry> fashion = Array.Empty<FashionEntry>();
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (field == 1 && wire == 0 && WireProtocol.TryReadVarint(payload, ref pos, out var cid)) charId = (long)cid;
            else if (wire == 2 && WireProtocol.TryReadLengthDelimited(payload, ref pos, out var inner))
            {
                switch (field)
                {
                    case 3:  ReadBasic(inner, ref name, ref level); break;
                    case 6:  professionId = (int)ReadFirstVarintField(inner, 1); break;
                    case 7:  ReadEquip(inner, gear); break;
                    case 8:  fashion = AttrFashionDataReader.Read(inner); break;
                    case 11: fightPoint = ReadFirstVarintField(inner, 4); break;
                    case 12: partySize = (int)ReadFirstVarintField(inner, 4); break;     // team_data.team_num
                    case 13: guild = ReadFirstStringField(inner, 2); break;              // union_data.name
                    case 16: ReadPersonalZone(inner, out titleId, out fashionCollect, out rideCollect, out weaponSkinCollect); break; // personal_zone
                    case 22: masterScore = ReadMasterScore(inner); break;
                    case 4:  AvatarInfoReader.Read(inner, out profileUrl, out halfBodyUrl); break;
                }
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        // Require BOTH a real char_id and a name. The login connection carries many Return shapes; the looser
        // gate (char OR name OR gear) false-matched a non-social Return that contained the self name → decoded
        // as char=0 with a garbled name and consumed the diagnostic one-shot. A genuine GetSocialDataReply
        // always carries SocialData.char_id (field 1) plus basic_data.name.
        if (charId == 0 || name.Length == 0) return null;
        gear.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
        return new SocialSnapshot(charId, name, level, fightPoint, professionId, gear, fashion,
            new SocialIdentity(guild, partySize, masterScore, titleId, fashionCollect, rideCollect, weaponSkinCollect),
            profileUrl, halfBodyUrl);
    }

    // personal_zone { title_id = 11, fashion_collect_point = 13, ride_collect_point = 18,
    // weapon_skin_collect_point = 20 }. Single walk over the submessage — mirrors ReadEquip/ReadBasic style.
    private static void ReadPersonalZone(ReadOnlySpan<byte> p, out int titleId, out int fashionCollect, out int rideCollect, out int weaponSkinCollect)
    {
        titleId = 0; fashionCollect = 0; rideCollect = 0; weaponSkinCollect = 0;
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (w == 0 && WireProtocol.TryReadVarint(p, ref pos, out var v))
            {
                switch (f)
                {
                    case 11: titleId = (int)v; break;
                    case 13: fashionCollect = (int)v; break;
                    case 18: rideCollect = (int)v; break;
                    case 20: weaponSkinCollect = (int)v; break;
                }
            }
            else if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
    }

    // master_mode_dungeon_data { season_score = 1, is_show = 2 }. The is_show flag is INVERTED on the
    // native ID card (truthy renders the score as "Hidden") — follow the game's privacy behaviour and
    // report 0 when the player hides it.
    private static int ReadMasterScore(ReadOnlySpan<byte> p)
        => ReadFirstVarintField(p, 2) != 0 ? 0 : (int)ReadFirstVarintField(p, 1);

    private static void ReadBasic(ReadOnlySpan<byte> p, ref string name, ref int level)
    {
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (f == 3 && w == 2 && WireProtocol.TryReadLengthDelimited(p, ref pos, out var s)) name = System.Text.Encoding.UTF8.GetString(s);
            else if (f == 6 && w == 0 && WireProtocol.TryReadVarint(p, ref pos, out var v)) level = (int)v;
            else if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
    }

    // First length-delimited occurrence of `target` decoded as UTF-8 (e.g. union_data.name).
    private static string ReadFirstStringField(ReadOnlySpan<byte> p, int target)
    {
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (f == target && w == 2 && WireProtocol.TryReadLengthDelimited(p, ref pos, out var s))
                return System.Text.Encoding.UTF8.GetString(s);
            if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
        return "";
    }

    // fight_point is int64; profession_id is int32 — return long and cast at the call site so large
    // ability scores never truncate.
    private static long ReadFirstVarintField(ReadOnlySpan<byte> p, int target)
    {
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (f == target && w == 0 && WireProtocol.TryReadVarint(p, ref pos, out var v)) return (long)v;
            if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
        return 0;
    }

    private static void ReadEquip(ReadOnlySpan<byte> p, List<GearSlotRef> gear)
    {
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (f == 1 && w == 2 && WireProtocol.TryReadLengthDelimited(p, ref pos, out var nine))
            {
                int slot = (int)ReadFirstVarintField(nine, 1), id = (int)ReadFirstVarintField(nine, 2);
                if (id != 0) gear.Add(new GearSlotRef(slot, id));
            }
            else if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
    }
}
