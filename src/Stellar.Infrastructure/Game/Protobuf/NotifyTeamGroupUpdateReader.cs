using System;
using System.Collections.Generic;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Parser for <c>NotifyTeamGroupUpdateRequest</c> — the raid group/slot layout pushed on
/// <c>GrpcTeamNtf</c> method 29 when a member's team or in-team position changes (drag in the raid-position
/// editor). The char-id order within each group IS the per-group slot order.
///
/// <code>
///   message NotifyTeamGroupUpdateRequest {
///     EErrorCode err_code = 1;                                  // skipped
///     map&lt;int32, TeamMemberGroupInfo&gt; team_member_group_infos = 2;
///   }
///   message TeamMemberGroupInfo { int32 group_id = 1; repeated int64 char_ids = 2; }
/// </code>
/// A proto map field encodes as repeated length-delimited entries {key=1, value=2}; the int32 map key is the
/// group index (ignored — the value carries the authoritative group_id).
/// </summary>
internal static class NotifyTeamGroupUpdateReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out IReadOnlyList<TeamGroupInfo> groups)
    {
        var list = new List<TeamGroupInfo>(4);
        int p = 0;
        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) { groups = list; return false; }
            if (field == 2 && wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var entry)) { groups = list; return false; }
                if (TeamGroupMapReader.TryReadEntry(entry, out var gi)) list.Add(gi);
            }
            else if (!WireProtocol.SkipField(payload, ref p, wire)) { groups = list; return false; }
        }
        groups = list;
        return true;
    }
}
