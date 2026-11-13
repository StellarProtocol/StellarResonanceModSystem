using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Wire;

public static partial class SocialDataReader
{
    // basic_data (zproto.BasicData) { name = 3, level = 6, personal_state = 8 (repeated int32),
    // offline_time = 9 (int64), online_time = 16 (int64) }. Same single walk as ReadBasic plus the presence trio.
    // A present basic_data always yields a presence: proto3 omits zeros, so an online player's reply has NO
    // offline_time at all — absence of field 9 is exactly "online", not "unknown".
    private static SocialPresence ReadBasicWithPresence(ReadOnlySpan<byte> p, ref string name, ref int level)
    {
        long offline = 0, online = 0;
        List<int>? states = null;   // allocated only when the player has a status set
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (f == 3 && w == 2 && WireProtocol.TryReadLengthDelimited(p, ref pos, out var s)) name = System.Text.Encoding.UTF8.GetString(s);
            else if (f == 8 && w == 2 && WireProtocol.TryReadLengthDelimited(p, ref pos, out var packed)) ReadPackedInt32(packed, ref states);
            else if (w == 0 && (f == 6 || f == 8 || f == 9 || f == 16) && WireProtocol.TryReadVarint(p, ref pos, out var v))
            {
                switch (f)
                {
                    case 6:  level = (int)v; break;
                    case 8:  (states ??= new List<int>(2)).Add((int)(long)v); break;   // unpacked repeated int32
                    case 9:  offline = (long)v; break;
                    case 16: online = (long)v; break;
                }
            }
            else if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
        return new SocialPresence(offline, online, (IReadOnlyList<int>?)states ?? Array.Empty<int>());
    }

    // proto3 encodes repeated scalars PACKED by default (one LEN field of back-to-back varints), but a parser must
    // accept both forms. int32 negatives arrive sign-extended to 10 bytes, hence the (int)(long) cast.
    private static void ReadPackedInt32(ReadOnlySpan<byte> p, ref List<int>? into)
    {
        int pos = 0;
        while (pos < p.Length && WireProtocol.TryReadVarint(p, ref pos, out var v))
            (into ??= new List<int>(2)).Add((int)(long)v);
    }

    // Top-level SocialData field → the section flag the cache merges on. Fields we don't decode map to None.
    private static SocialSections SectionFor(int field) => field switch
    {
        3  => SocialSections.Basic,
        4  => SocialSections.Avatar,
        6  => SocialSections.Profession,
        7  => SocialSections.Equip,
        8  => SocialSections.Fashion,
        10 => SocialSections.Scene,
        11 => SocialSections.Attr,
        12 => SocialSections.Team,
        13 => SocialSections.Union,
        16 => SocialSections.PersonalZone,
        22 => SocialSections.MasterMode,
        _  => SocialSections.None,
    };
}
