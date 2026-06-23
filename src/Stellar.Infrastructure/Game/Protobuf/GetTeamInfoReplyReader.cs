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
    // Mutable accumulator for the parse pass (keeps TryParseFields to a single param object).
    private sealed class Acc
    {
        public TeamBaseInfoMsg BaseInfo;
        public bool SawBaseInfo;
        public readonly Dictionary<long, PartyMemberRoster> Roster = new();
        public readonly Dictionary<long, PartyMemberFastSync> Fast = new();
        public readonly Dictionary<long, (int Mic, int Speak)> Voice = new();
    }

    private static bool TryParseReply(ReadOnlySpan<byte> payload, out PartyWireSnapshot snapshot)
    {
        snapshot = default!;
        var acc = new Acc();
        if (!TryParseFields(payload, acc)) return false;
        if (!acc.SawBaseInfo || acc.Roster.Count == 0) return false;

        var rosterList = new List<PartyMemberRoster>(acc.Roster.Count);
        foreach (var r in acc.Roster.Values)
        {
            var fs = acc.Fast.TryGetValue(r.CharId, out var f) ? f : null;
            var rr = r with { FastSync = fs };
            if (acc.Voice.TryGetValue(r.CharId, out var v))
                rr = rr with { MicStatusRaw = v.Mic, Speaking = v.Speak == 1 };
            rosterList.Add(rr);
        }

        snapshot = new PartyWireSnapshot(
            PartyId:      acc.BaseInfo.PartyId,
            LeaderCharId: acc.BaseInfo.LeaderCharId,
            PartyType:    acc.BaseInfo.PartyType,
            IsMatching:   acc.BaseInfo.IsMatching,
            Roster:       rosterList,
            Groups:       acc.BaseInfo.Groups);
        return true;
    }

    private static bool TryParseFields(ReadOnlySpan<byte> payload, Acc acc)
    {
        int p = 0;
        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) return false;
            switch ((field, wire))
            {
                case (1, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var biBytes)) return false;
                    if (!TeamBaseInfoReader.TryRead(biBytes, out acc.BaseInfo)) return false;
                    acc.SawBaseInfo = true;
                    break;
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var memBytes)) return false;
                    if (!TeamMemDataReader.TryRead(memBytes, out var roster)) return false;
                    acc.Roster[roster.CharId] = roster;
                    break;
                case (4, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var voiceBytes)) return false;
                    if (!TryReadVoiceMapEntry(voiceBytes, out var vKey, out var vVal)) return false;
                    acc.Voice[vKey] = vVal;
                    break;
                case (7, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var entryBytes)) return false;
                    if (!TryReadFastSyncMapEntry(entryBytes, out var key, out var fast)) return false;
                    acc.Fast[key] = fast;
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) return false;
                    break;
            }
        }
        return true;
    }

    // map<int64, TeamMemRealTimeVoiceInfo> entry: {key=1 int64, value=2 {microphone_status=1, speak_status=2}}
    private static bool TryReadVoiceMapEntry(ReadOnlySpan<byte> payload, out long key, out (int Mic, int Speak) value)
    {
        key = 0; value = default;
        int p = 0, mic = 0, speak = 0;
        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) return false;
            if (field == 1 && wire == 0)
            { if (!WireProtocol.TryReadVarint(payload, ref p, out var k)) return false; key = (long)k; }
            else if (field == 2 && wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var inner)) return false;
                int q = 0;
                while (q < inner.Length)
                {
                    if (!WireProtocol.TryReadTag(inner, ref q, out var f, out var w)) return false;
                    if (f == 1 && w == 0) { if (!WireProtocol.TryReadVarint(inner, ref q, out var mv)) return false; mic = (int)mv; }
                    else if (f == 2 && w == 0) { if (!WireProtocol.TryReadVarint(inner, ref q, out var sv)) return false; speak = (int)sv; }
                    else if (!WireProtocol.SkipField(inner, ref q, w)) return false;
                }
            }
            else if (!WireProtocol.SkipField(payload, ref p, wire)) return false;
        }
        value = (mic, speak);
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
