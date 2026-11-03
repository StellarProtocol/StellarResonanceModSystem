using System;

namespace Stellar.Wire;

/// <summary>
/// Pure parser for the WorldNtf notify 43 <c>SyncServerTime</c> body — the server's answer to the
/// client's World <c>ReqServerTime</c> (method 5, sent every 5000 ms):
/// <code>
///   message SyncServerTime {
///     int64 client_milliseconds = 1;   // OUR OWN PC timestamp, echoed back by the server
///     int64 server_milliseconds = 2;   // the real server clock  ← anchored
///   }
/// </code>
/// Both are Unix-epoch ms varints.
///
/// <para>
/// ⚠ Field 1 is NOT server time. The probe used to anchor on it, which made
/// <c>ICombatSnapshot.ServerNowMs</c> the PC wall clock minus one round trip — measured 2026-11-03
/// 1.72 s behind the game's own Lua <c>Z.ServerTime</c> (which stores field 2) on a PC whose clock was
/// 1.7 s slow. See Knowledge Base <c>Server-Clock-and-Ping-HUD.md</c>.
/// </para>
/// </summary>
public static class SyncServerTimeReader
{
    /// <summary>
    /// Read <c>server_milliseconds</c> (field 2) from a SyncServerTime body, in any field order, skipping
    /// unknown fields. When field 2 is absent or ≤ 0, falls back to <c>client_milliseconds</c> (field 1) so the
    /// clock keeps the old behaviour instead of staying at 0. Returns false when neither is present and positive.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> payload, out long epochMs)
    {
        long client = 0, server = 0;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if ((field == 1 || field == 2) && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(payload, ref pos, out var v)) break;
                if (field == 1) client = (long)v; else server = (long)v;
                continue;
            }
            if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }

        epochMs = server > 0 ? server : client;
        return epochMs > 0;
    }
}
