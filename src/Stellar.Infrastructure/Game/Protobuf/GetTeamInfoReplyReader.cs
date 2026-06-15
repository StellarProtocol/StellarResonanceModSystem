using System;
using System.Collections.Generic;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Decodes <c>GetTeamInfoReply</c> — the reply to <c>GetTeamInfoRequest</c> that
/// the client issues on login / opening the party panel. It carries the FULL party
/// roster, which the incremental <c>GrpcTeamNtf</c> notifies do not deliver on a
/// fresh login (no "join" fires for pre-existing members). Arrives as a Return on
/// the login connection, so the wiretap surfaces it via <c>RegisterReturn</c>.
///
/// <para>Wire shape: the server wraps the reply inside a <c>GetTeamInfo_Ret</c>
/// envelope (field 1 = <c>GetTeamInfoReply</c> message), so the actual payload
/// has one extra length-delimited layer. <c>TryRead</c> checks the first tag to
/// fast-reject unrelated Returns before any full walk or dictionary allocation,
/// then unwraps field 1 in-place (no byte[] copy) and parses its sub-span as
/// <c>GetTeamInfoReply</c>, falling back to a direct parse for the defensive case.</para>
///
/// <para>A result is accepted only when <c>base_info</c> (field 1) was present
/// AND at least one <c>member_data</c> (field 2) decoded successfully. This rejects
/// base-info-only frames and unrelated social/friend Returns whose inner field 1 is
/// a varint rather than a <c>TeamBaseInfo</c> message.</para>
/// </summary>
internal static class GetTeamInfoReplyReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out PartyWireSnapshot snapshot)
    {
        snapshot = default!;

        // Cheap structural gate: a GetTeamInfo_Ret envelope (ret=1) and a bare
        // GetTeamInfoReply (base_info=1) both lead with field 1, wire-type 2.
        // Any other Return cannot be a team reply — reject with zero allocation
        // before any full walk / dictionary allocation. (Runs per-Return on the
        // network thread, so this fast reject is the hot path.)
        // NOTE: field 1 arriving first is true in all observed captures
        // (the game emits base_info/ret first); revisit if proto field ordering changes.
        int p = 0;
        if (!WireProtocol.TryReadTag(payload, ref p, out var f0, out var w0)) return false;
        if (f0 != 1 || w0 != 2) return false;

        // Case A — GetTeamInfo_Ret { GetTeamInfoReply ret = 1 }: parse field 1 in
        // place (sub-span over the same buffer, no copy).
        if (WireProtocol.TryReadLengthDelimited(payload, ref p, out var inner)
            && TryParseReply(inner, out snapshot)) return true;

        // Case B — payload already IS GetTeamInfoReply (defensive / unwrapped).
        return TryParseReply(payload, out snapshot);
    }

    /// <summary>
    /// Parses bytes as a <c>GetTeamInfoReply</c>. Returns true only when
    /// <c>base_info</c> was present AND the roster contains at least one member.
    /// </summary>
    private static bool TryParseReply(ReadOnlySpan<byte> payload, out PartyWireSnapshot snapshot)
    {
        snapshot = default!;
        TeamBaseInfoMsg baseInfo = default;
        bool sawBaseInfo = false;
        var rosterByCharId = new Dictionary<long, PartyMemberRoster>();
        var fastByCharId   = new Dictionary<long, PartyMemberFastSync>();

        if (!TryParseFields(payload, ref baseInfo, ref sawBaseInfo, rosterByCharId, fastByCharId))
            return false;

        if (!sawBaseInfo || rosterByCharId.Count == 0)
            return false;

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

    private static bool TryParseFields(
        ReadOnlySpan<byte> payload,
        ref TeamBaseInfoMsg baseInfo,
        ref bool sawBaseInfo,
        Dictionary<long, PartyMemberRoster> rosterByCharId,
        Dictionary<long, PartyMemberFastSync> fastByCharId)
    {
        int p = 0;
        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) return false;
            switch ((field, wire))
            {
                case (1, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var biBytes)) return false;
                    if (!TeamBaseInfoReader.TryRead(biBytes, out baseInfo)) return false;
                    sawBaseInfo = true;
                    break;
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var memBytes)) return false;
                    if (!TeamMemDataReader.TryRead(memBytes, out var roster)) return false;
                    rosterByCharId[roster.CharId] = roster;
                    break;
                case (7, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var entryBytes)) return false;
                    if (!TryReadFastSyncMapEntry(entryBytes, out var key, out var fast)) return false;
                    fastByCharId[key] = fast;
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) return false;
                    break;
            }
        }
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
