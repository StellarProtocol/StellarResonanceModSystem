using System;
using System.Collections.Generic;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Reads a <c>zproto.stru_notify_join_team_request</c> message.
/// <code>
///   message stru_notify_join_team_request {
///     TeamBaseInfo                              base_info            = 1;
///     repeated TeamMemData                      member_data          = 2;
///     map&lt;int64, TeamMemRealTimeVoiceInfo&gt;  mem_real_time_voice_infos = 4;  // skipped
///     ETeamJoinType                             team_join_type       = 5;
///     map&lt;int64, TeamMemberFastSyncData&gt;    member_sync_datas    = 6;
///   }
/// </code>
/// </summary>
internal static class NotifyJoinTeamReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out PartyWireSnapshot snapshot)
    {
        TeamBaseInfoMsg baseInfo = default;
        var rosterByCharId = new Dictionary<long, PartyMemberRoster>();
        var fastByCharId   = new Dictionary<long, PartyMemberFastSync>();
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
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var memBytes)) { snapshot = default!; return false; }
                    if (!TeamMemDataReader.TryRead(memBytes, out var roster)) { snapshot = default!; return false; }
                    rosterByCharId[roster.CharId] = roster;
                    break;
                case (6, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var entryBytes)) { snapshot = default!; return false; }
                    if (!TryReadFastSyncMapEntry(entryBytes, out var key, out var fast)) { snapshot = default!; return false; }
                    fastByCharId[key] = fast;
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) { snapshot = default!; return false; }
                    break;
            }
        }

        var rosterList = new List<PartyMemberRoster>(rosterByCharId.Count);
        foreach (var r in rosterByCharId.Values)
        {
            var fs = fastByCharId.TryGetValue(r.CharId, out var f) ? f : null;
            rosterList.Add(r with { FastSync = fs });
        }

        snapshot = new PartyWireSnapshot(
            PartyId:      baseInfo.PartyId,
            LeaderCharId: baseInfo.LeaderCharId,
            PartyType:    baseInfo.PartyType,
            IsMatching:   baseInfo.IsMatching,
            Roster:       rosterList,
            Groups:       baseInfo.Groups);
        return true;
    }

    private static bool TryReadFastSyncMapEntry(
        ReadOnlySpan<byte> payload,
        out long key,
        out PartyMemberFastSync value)
    {
        key = 0;
        value = default!;
        int p = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) return false;
            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref p, out var k)) return false;
                    key = (long)k;
                    break;
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var inner)) return false;
                    if (!TeamMemberFastSyncDataReader.TryRead(inner, out _, out value)) return false;
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) return false;
                    break;
            }
        }
        return true;
    }
}
