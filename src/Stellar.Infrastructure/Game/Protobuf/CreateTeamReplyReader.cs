using System;
using System.Collections.Generic;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Decodes <c>CreateTeam_Ret</c> — the reply to creating a party. Unlike
/// <c>GetTeamInfoReply</c> (which the game only sends when the party panel is opened),
/// this arrives the moment you CREATE a party, so parsing it lets the 5/20 control
/// appear on create without needing to revisit the party page.
///
/// <para>Wire shape differs from GetTeamInfoReply: the id/leader are nested inside a
/// <c>TeamInfo</c> wrapper rather than at the top level —
/// <c>CreateTeam_Ret { CreateTeamReply ret=1 } → CreateTeamReply { TeamInfo team_info=1 } →
/// TeamInfo { int64 team_id=1; map members=2; TeamBaseInfo base_info=3 }</c>. We only need
/// the identity (<c>base_info</c>); the roster is filled by the GrpcTeamNtf member-sync, so
/// the returned snapshot carries an empty roster (which never prunes — it only adopts identity).</para>
///
/// <para>Accepted ONLY when the outer <c>team_id</c> (TeamInfo field 1) equals
/// <c>base_info</c>'s team id and both are non-zero with a leader — a structural invariant a
/// real TeamInfo always satisfies but unrelated Returns do not, so this can't false-positive.
/// The combat-meter's leader gate is the second guard.</para>
/// </summary>
internal static class CreateTeamReplyReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out PartyWireSnapshot snapshot)
    {
        // TeamInfo may be the payload itself, or wrapped 1–2 length-delimited layers deep
        // (the CreateTeamReply, then the CreateTeam_Ret envelope). Peel each leading field-1
        // submessage in its own scope and try to parse as TeamInfo (sub-spans used in place,
        // never assigned to an outer-scoped span, to satisfy ref-safety).
        if (TryParseTeamInfo(payload, out snapshot)) return true;

        int p1 = 0;
        if (WireProtocol.TryReadTag(payload, ref p1, out var f1, out var w1) && f1 == 1 && w1 == 2
            && WireProtocol.TryReadLengthDelimited(payload, ref p1, out var l1))
        {
            if (TryParseTeamInfo(l1, out snapshot)) return true;

            int p2 = 0;
            if (WireProtocol.TryReadTag(l1, ref p2, out var f2, out var w2) && f2 == 1 && w2 == 2
                && WireProtocol.TryReadLengthDelimited(l1, ref p2, out var l2)
                && TryParseTeamInfo(l2, out snapshot)) return true;
        }

        snapshot = default!;
        return false;
    }

    // Parse a span as TeamInfo { team_id=1, base_info=3 } and build an identity-only snapshot.
    private static bool TryParseTeamInfo(ReadOnlySpan<byte> span, out PartyWireSnapshot snapshot)
    {
        snapshot = default!;
        long teamId = 0;
        TeamBaseInfoMsg baseInfo = default;
        bool sawBase = false;
        int p = 0;
        while (p < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref p, out var field, out var wire)) return false;
            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(span, ref p, out var tid)) return false;
                    teamId = (long)tid;
                    break;
                case (3, 2):
                    if (!WireProtocol.TryReadLengthDelimited(span, ref p, out var bi)) return false;
                    if (!TeamBaseInfoReader.TryRead(bi, out baseInfo)) return false;
                    sawBase = true;
                    break;
                default:
                    if (!WireProtocol.SkipField(span, ref p, wire)) return false;
                    break;
            }
        }

        if (!sawBase || teamId == 0 || baseInfo.PartyId != teamId || baseInfo.LeaderCharId == 0) return false;

        snapshot = new PartyWireSnapshot(
            PartyId:      baseInfo.PartyId,
            LeaderCharId: baseInfo.LeaderCharId,
            PartyType:    baseInfo.PartyType,
            IsMatching:   baseInfo.IsMatching,
            Roster:       new List<PartyMemberRoster>(),
            Groups:       baseInfo.Groups);
        return true;
    }
}
