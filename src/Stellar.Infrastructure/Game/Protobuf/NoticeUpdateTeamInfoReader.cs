using System;
using System.Collections.Generic;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Reads a <c>zproto.notice_update_team_info</c> message.
/// Only <c>base_info</c> (field 1) is consumed; all other fields are skipped.
/// <code>
///   message notice_update_team_info {
///     TeamBaseInfo base_info = 1;
///     // additional fields (leader change, etc.) — future use
///   }
/// </code>
/// </summary>
internal static class NoticeUpdateTeamInfoReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out PartyWireSnapshot snapshot)
    {
        TeamBaseInfoMsg baseInfo = default;
        int p = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire))
            { snapshot = default!; return false; }

            switch ((field, wire))
            {
                case (1, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var biBytes)) { snapshot = default!; return false; }
                    if (!TeamBaseInfoReader.TryRead(biBytes, out baseInfo)) { snapshot = default!; return false; }
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) { snapshot = default!; return false; }
                    break;
            }
        }

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
