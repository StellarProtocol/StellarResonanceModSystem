using System;
using System.Collections.Generic;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Parsers for the GrpcTeamNtf team-voice pushes (both arrive wrapped in a
/// <c>v_request</c> field — unwrap with <see cref="WireProtocol.TryReadVRequest"/> first).
///
/// <code>
///   // method 25
///   message NotifyTeamMemMicrophoneStatusChangeRequest { int64 member_id=1; EMicrophoneStatus microphone_status=2; }
///   // method 26
///   message NotifyTeamMemsSpeakStatusChangeRequest { map&lt;int64, ESpeakStatus&gt; mem_speak_status=1; }
/// </code>
/// </summary>
internal static class NotifyTeamMemMicrophoneStatusReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out long charId, out int micStatusRaw)
    {
        charId = 0; micStatusRaw = 0;
        int p = 0;
        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) return false;
            if (field == 1 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) return false;
                charId = (long)v;
            }
            else if (field == 2 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) return false;
                micStatusRaw = (int)v;
            }
            else if (!WireProtocol.SkipField(payload, ref p, wire)) return false;
        }
        return charId != 0;
    }
}

/// <summary>Parser for <c>NotifyTeamMemsSpeakStatusChangeRequest</c> (method 26). The proto map encodes
/// as repeated length-delimited entries {key=1 int64, value=2 enum}.</summary>
internal static class NotifyTeamMemsSpeakStatusReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out IReadOnlyList<(long CharId, int Raw)> entries)
    {
        var list = new List<(long, int)>(4);
        int p = 0;
        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)) { entries = list; return false; }
            if (field == 1 && wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var entry)) { entries = list; return false; }
                if (TryReadEntry(entry, out var c, out var s)) list.Add((c, s));
            }
            else if (!WireProtocol.SkipField(payload, ref p, wire)) { entries = list; return false; }
        }
        entries = list;
        return true;
    }

    private static bool TryReadEntry(ReadOnlySpan<byte> e, out long charId, out int raw)
    {
        charId = 0; raw = 0;
        int p = 0;
        while (p < e.Length)
        {
            if (!WireProtocol.TryReadTag(e, ref p, out var field, out var wire)) return false;
            if (field == 1 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(e, ref p, out var v)) return false;
                charId = (long)v;
            }
            else if (field == 2 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(e, ref p, out var v)) return false;
                raw = (int)v;
            }
            else if (!WireProtocol.SkipField(e, ref p, wire)) return false;
        }
        return true;
    }
}
