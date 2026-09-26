using System;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Runs one WorldNtf packet's handler inside the combat sink's packet bracket
/// (<see cref="ICombatEventIngestion.BeginPacket"/> / <see cref="ICombatEventIngestion.EndPacket"/>), releasing it
/// in a <c>finally</c> so a throwing handler can never leave the bracket held — a leak would skip every later
/// spec publish and block a logout reset (spec-from-talent-buffs final review round, 2026-09-26).
/// </summary>
internal static class IngestBracket
{
    public static void Run(ICombatEventIngestion sink, uint methodId, byte[] payload, Action<uint, byte[]> route)
    {
        sink.BeginPacket();
        try { route(methodId, payload); }
        finally { sink.EndPacket(); }
    }
}
