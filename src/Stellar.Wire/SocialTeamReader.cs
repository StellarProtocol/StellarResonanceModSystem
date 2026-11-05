using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Wire;

/// <summary>Pure parser for <c>SocialData.team_data</c> (field 12, <c>zproto.CharTeam</c>) into a
/// <see cref="SocialParty"/>. Defensive: malformed input yields what parsed so far, never throws.
/// A present-but-empty section still yields a (zeroed) party — the caller keeps that distinct from an
/// absent section (null), which is what the <c>[TeamDiag]</c> probe measures.</summary>
public static class SocialTeamReader
{
    // CharTeam { team_id = 1 int64, leader_id = 2 int64, team_target_id = 3 uint32, team_num = 4 uint32,
    // char_ids = 5 repeated int64, is_matching = 6, char_team_version = 7,
    // team_member_data = 8 map<int64, TeamMemData>, team_member_type = 9 ETeamMemberType }.
    public static SocialParty Read(ReadOnlySpan<byte> p)
    {
        long teamId = 0, leaderId = 0; int target = 0, num = 0, memberType = 0;
        var charIds = new List<long>();
        var members = new List<SocialPartyMember>();
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (w == 0 && WireProtocol.TryReadVarint(p, ref pos, out var v))
            {
                switch (f)
                {
                    case 1: teamId = (long)v; break;
                    case 2: leaderId = (long)v; break;
                    case 3: target = (int)v; break;
                    case 4: num = (int)v; break;
                    case 5: charIds.Add((long)v); break;     // unpacked repeated encoding
                    case 9: memberType = (int)v; break;
                }
            }
            else if (w == 2 && WireProtocol.TryReadLengthDelimited(p, ref pos, out var inner))
            {
                // proto3 packs repeated scalars by default, but the server's encoder isn't confirmed to —
                // field 5 accepts BOTH the packed (one LEN blob of varints) and the unpacked (varint per tag) form.
                if (f == 5) ReadPackedInt64(inner, charIds);
                else if (f == 8 && ReadMemberEntry(inner) is { } m) members.Add(m);
            }
            else if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
        return new SocialParty(teamId, leaderId, target, num, memberType, charIds, members);
    }

    private static void ReadPackedInt64(ReadOnlySpan<byte> p, List<long> into)
    {
        int pos = 0;
        while (pos < p.Length && WireProtocol.TryReadVarint(p, ref pos, out var v)) into.Add((long)v);
    }

    // Map entry { key = 1 int64, value = 2 TeamMemData }. The key is the member's char id; used as the
    // fallback when the value omits its own char_id (proto3 default 0 is never sent).
    private static SocialPartyMember? ReadMemberEntry(ReadOnlySpan<byte> p)
    {
        long key = 0; SocialPartyMember? value = null;
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (f == 1 && w == 0 && WireProtocol.TryReadVarint(p, ref pos, out var k)) key = (long)k;
            else if (f == 2 && w == 2 && WireProtocol.TryReadLengthDelimited(p, ref pos, out var inner)) value = ReadMember(inner);
            else if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
        if (value is not { } m) return key != 0 ? new SocialPartyMember(key, "", 0, 0, 0, 0, 0, 0, 0) : null;
        return m.CharId == 0 ? m with { CharId = key } : m;
    }

    // TeamMemData { char_id = 1 int64, enter_time = 2 uint32, call_status = 3, talent_id = 4 int32,
    // online_status = 5 int32, scene_id = 6 int32, voice_is_open = 7, group_id = 8,
    // social_data = 9 TeamMemberSocialData }.
    private static SocialPartyMember ReadMember(ReadOnlySpan<byte> p)
    {
        long charId = 0, fightPoint = 0; uint enterTime = 0;
        int talent = 0, online = 0, scene = 0, level = 0, prof = 0; string name = "";
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (w == 0 && WireProtocol.TryReadVarint(p, ref pos, out var v))
            {
                switch (f)
                {
                    case 1: charId = (long)v; break;
                    case 2: enterTime = (uint)v; break;
                    case 4: talent = (int)(long)v; break;   // int32: negatives arrive sign-extended
                    case 5: online = (int)(long)v; break;
                    case 6: scene = (int)(long)v; break;
                }
            }
            else if (f == 9 && w == 2 && WireProtocol.TryReadLengthDelimited(p, ref pos, out var social))
                ReadMemberSocial(social, ref name, ref level, ref prof, ref fightPoint);
            else if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
        return new SocialPartyMember(charId, name, level, prof, talent, fightPoint, online, scene, enterTime);
    }

    // TeamMemberSocialData { basic_data = 1, profession_data = 4, user_attr_data = 8, others ignored } —
    // the same sub-messages SocialData carries at 3/6/11, so the SocialDataReader helpers apply as-is.
    private static void ReadMemberSocial(ReadOnlySpan<byte> p, ref string name, ref int level, ref int prof, ref long fightPoint)
    {
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (w == 2 && WireProtocol.TryReadLengthDelimited(p, ref pos, out var inner))
            {
                switch (f)
                {
                    case 1: SocialDataReader.ReadBasic(inner, ref name, ref level); break;
                    case 4: prof = (int)SocialDataReader.ReadFirstVarintField(inner, 1); break;
                    case 8: fightPoint = SocialDataReader.ReadFirstVarintField(inner, 4); break;
                }
            }
            else if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
    }
}
