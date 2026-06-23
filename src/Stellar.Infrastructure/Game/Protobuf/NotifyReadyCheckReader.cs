using System;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Parsers for the WorldNtf dungeon ready-check pushes.
///
/// <code>
///   message NotifyAllMemberReady { bool v_open_or_close = 1; }              // method 70
///   message NotifyCaptainReady {                                            // method 71
///     string v_member_name = 1;
///     int64  v_char_id      = 2;
///     DungeonReadyInfo v_ready_info = 3;                                    // is_ready = 1
///   }
/// </code>
/// Payload is the raw <c>IStubCall.GetCallData()</c> bytes — decoded directly
/// (no <c>v_request</c> wrapper, unlike the GrpcTeamNtf notifies).
/// </summary>
internal static class NotifyReadyCheckReader
{
    /// <summary>Parses <c>NotifyCaptainReady</c> (method 71). Returns false if the char id is missing.</summary>
    public static bool TryReadCaptainReady(ReadOnlySpan<byte> payload, out long charId, out string? name, out bool isReady)
    {
        charId = 0; name = null; isReady = false;
        int p = 0;
        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) return false;
            if (field == 1 && wire == 2)
            {
                if (!WireProtocol.TryReadString(payload, ref p, out var n)) return false;
                name = n;
            }
            else if (field == 2 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(payload, ref p, out var cid)) return false;
                charId = (long)cid;
            }
            else if (field == 3 && wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var inner)) return false;
                isReady = ReadIsReady(inner);
            }
            else if (!WireProtocol.SkipField(payload, ref p, wire)) return false;
        }
        return charId != 0;
    }

    /// <summary>Parses <c>NotifyAllMemberReady</c> (method 70). A missing field 1 (proto3 default)
    /// means close — <paramref name="isOpen"/> is left false.</summary>
    public static bool TryReadAllMemberReady(ReadOnlySpan<byte> payload, out bool isOpen)
    {
        isOpen = false;
        int p = 0;
        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) return false;
            if (field == 1 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) return false;
                isOpen = v != 0;
            }
            else if (!WireProtocol.SkipField(payload, ref p, wire)) return false;
        }
        return true;
    }

    // DungeonReadyInfo.is_ready = field 1 (bool).
    private static bool ReadIsReady(ReadOnlySpan<byte> info)
    {
        int p = 0;
        while (p < info.Length)
        {
            if (!WireProtocol.TryReadTag(info, ref p, out var field, out var wire)) return false;
            if (field == 1 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(info, ref p, out var v)) return false;
                return v != 0;
            }
            if (!WireProtocol.SkipField(info, ref p, wire)) return false;
        }
        return false;
    }
}
