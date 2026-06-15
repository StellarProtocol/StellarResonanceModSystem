using System;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Reads a <c>zproto.stru_notify_leave_team_request</c> (or notify) message.
/// <code>
///   message stru_notify_leave_team_request {
///     int64        char_id    = 1;
///     ETeamLeaveType leave_type = 2;
///   }
/// </code>
/// </summary>
internal readonly record struct NotifyLeaveTeamMsg(long CharId, int LeaveTypeRaw);

internal static class NotifyLeaveTeamReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out NotifyLeaveTeamMsg msg)
    {
        long charId    = 0;
        int  leaveType = 0;
        int  p         = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire))
            { msg = default; return false; }

            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref p, out var v1)) { msg = default; return false; }
                    charId = (long)v1;
                    break;
                case (2, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref p, out var v2)) { msg = default; return false; }
                    leaveType = (int)v2;
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) { msg = default; return false; }
                    break;
            }
        }

        msg = new NotifyLeaveTeamMsg(charId, leaveType);
        return true;
    }
}
