using System;
using System.Collections.Generic;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game.Protobuf;

internal readonly record struct NoticeUpdateTeamMemberInfoMsg(
    IReadOnlyList<FastSyncEntry>   FastSyncs,
    IReadOnlyList<SocialSyncEntry> SocialSyncs);

internal readonly record struct FastSyncEntry(long CharId, PartyMemberFastSync Data);
internal readonly record struct SocialSyncEntry(long CharId, PartyMemberRoster Roster);

internal static class NoticeUpdateTeamMemberInfoReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out NoticeUpdateTeamMemberInfoMsg msg)
    {
        var fasts = new List<FastSyncEntry>(4);
        var socials = new List<SocialSyncEntry>(4);
        int p = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire))
            { msg = default; return false; }

            switch ((field, wire))
            {
                case (5, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var fastBytes)) { msg = default; return false; }
                    if (!TeamMemberFastSyncDataReader.TryRead(fastBytes, out var charId, out var data)) { msg = default; return false; }
                    fasts.Add(new FastSyncEntry(charId, data));
                    break;
                case (6, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var socBytes)) { msg = default; return false; }
                    if (!TeamMemDataReader.TryRead(socBytes, out var roster)) { msg = default; return false; }
                    socials.Add(new SocialSyncEntry(roster.CharId, roster));
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) { msg = default; return false; }
                    break;
            }
        }

        msg = new NoticeUpdateTeamMemberInfoMsg(fasts, socials);
        return true;
    }
}
