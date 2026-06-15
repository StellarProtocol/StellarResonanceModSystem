using System;
using System.Collections.Generic;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Shared parser for the <c>map&lt;int32, TeamMemberGroupInfo&gt;</c> that carries the raid group/slot layout —
/// it appears both in <c>NotifyTeamGroupUpdateRequest.team_member_group_infos</c> (method 29) and in
/// <c>TeamBaseInfo.team_member_group_infos</c> (field 9, present in the full GetTeamInfo / NoticeUpdateTeamInfo
/// fetch at login). A proto map encodes as repeated length-delimited entries {key=1, value=2}; the int32 key is
/// the group index (ignored — the value's <c>group_id</c> is authoritative). The char-id order within a group
/// IS the per-group slot order.
/// </summary>
internal static class TeamGroupMapReader
{
    // Parse one map entry: { int32 key = 1; TeamMemberGroupInfo value = 2 }.
    public static bool TryReadEntry(ReadOnlySpan<byte> e, out TeamGroupInfo gi)
    {
        gi = default;
        int p = 0, groupId = 0;
        var charIds = new List<long>(5);
        while (p < e.Length)
        {
            if (!WireProtocol.TryReadTag(e, ref p, out var f, out var w)) return false;
            if (f == 2 && w == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(e, ref p, out var val)) return false;
                if (!TryReadGroupInfo(val, ref groupId, charIds)) return false;
            }
            else if (f == 1 && w == 0)
            {
                if (!WireProtocol.TryReadVarint(e, ref p, out _)) return false;   // map key (group index) — ignored
            }
            else if (!WireProtocol.SkipField(e, ref p, w)) return false;
        }
        gi = new TeamGroupInfo(groupId, charIds);
        return true;
    }

    // TeamMemberGroupInfo { int32 group_id = 1; repeated int64 char_ids = 2; }
    private static bool TryReadGroupInfo(ReadOnlySpan<byte> v, ref int groupId, List<long> charIds)
    {
        int p = 0;
        while (p < v.Length)
        {
            if (!WireProtocol.TryReadTag(v, ref p, out var f, out var w)) return false;
            if (f == 1 && w == 0)
            {
                if (!WireProtocol.TryReadVarint(v, ref p, out var g)) return false;
                groupId = (int)g;
            }
            else if (f == 2 && w == 2)   // packed repeated int64 (proto3 default)
            {
                if (!WireProtocol.TryReadLengthDelimited(v, ref p, out var packed)) return false;
                int q = 0;
                while (q < packed.Length)
                {
                    if (!WireProtocol.TryReadVarint(packed, ref q, out var c)) return false;
                    charIds.Add((long)c);
                }
            }
            else if (f == 2 && w == 0)   // unpacked repeated int64 (defensive)
            {
                if (!WireProtocol.TryReadVarint(v, ref p, out var c)) return false;
                charIds.Add((long)c);
            }
            else if (!WireProtocol.SkipField(v, ref p, w)) return false;
        }
        return true;
    }
}
