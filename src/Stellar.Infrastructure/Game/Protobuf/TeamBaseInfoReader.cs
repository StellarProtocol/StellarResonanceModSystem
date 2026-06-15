using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game.Protobuf;

internal readonly record struct TeamBaseInfoMsg(
    long      PartyId,
    long      LeaderCharId,
    PartyType PartyType,
    bool      IsMatching,
    IReadOnlyList<TeamGroupInfo> Groups);

internal static class TeamBaseInfoReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out TeamBaseInfoMsg msg)
    {
        long teamId = 0;
        long leaderId = 0;
        bool matching = false;
        PartyType partyType = PartyType.Solo;
        List<TeamGroupInfo>? groups = null;
        int p = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire))
            { msg = default; return false; }

            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref p, out var v1)) { msg = default; return false; }
                    teamId = (long)v1;
                    break;
                case (3, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref p, out var v3)) { msg = default; return false; }
                    leaderId = (long)v3;
                    break;
                case (7, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref p, out var v7)) { msg = default; return false; }
                    matching = v7 != 0;
                    break;
                case (8, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref p, out var v8)) { msg = default; return false; }
                    partyType = (int)v8 == 1 ? PartyType.Raid20 : PartyType.Regular5;
                    break;
                case (9, 2):   // map<int32, TeamMemberGroupInfo> team_member_group_infos — raid group/slot layout
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var entry)) { msg = default; return false; }
                    if (TeamGroupMapReader.TryReadEntry(entry, out var gi)) (groups ??= new List<TeamGroupInfo>(4)).Add(gi);
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) { msg = default; return false; }
                    break;
            }
        }

        msg = new TeamBaseInfoMsg(teamId, leaderId, partyType, matching, groups ?? (IReadOnlyList<TeamGroupInfo>)Array.Empty<TeamGroupInfo>());
        return true;
    }
}
