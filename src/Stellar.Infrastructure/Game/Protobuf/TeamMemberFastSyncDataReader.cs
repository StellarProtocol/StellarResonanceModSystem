using System;
using Stellar.Abstractions.Domain;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Reads a <c>zproto.TeamMemberFastSyncData</c> sub-message.
/// <code>
///   message TeamMemberFastSyncData {
///     int64    char_id      = 1;
///     int32    scene_id     = 2;
///     Position position     = 3;
///     int64    hp           = 4;
///     int64    max_hp       = 5;
///     int32    state        = 6;
///     int32    scene_area_id = 7;
///   }
/// </code>
/// </summary>
internal static class TeamMemberFastSyncDataReader
{
    public static bool TryRead(
        ReadOnlySpan<byte>   payload,
        out long             charId,
        out PartyMemberFastSync data)
    {
        long       cid        = 0;
        int        sceneId    = 0;
        Position3D position   = Position3D.Zero;
        long       hp         = 0;
        long       maxHp      = 0;
        int        state      = 0;
        int        p          = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire))
            { charId = default; data = default!; return false; }

            switch ((field, wire))
            {
                case (1, 0): if (!TryReadCharId(payload, ref p, out cid))           { charId = default; data = default!; return false; } break;
                case (2, 0): if (!TryReadSceneId(payload, ref p, out sceneId))      { charId = default; data = default!; return false; } break;
                case (3, 2): if (!TryReadPosition(payload, ref p, out position))    { charId = default; data = default!; return false; } break;
                case (4, 0): if (!TryReadHp(payload, ref p, out hp))                { charId = default; data = default!; return false; } break;
                case (5, 0): if (!TryReadMaxHp(payload, ref p, out maxHp))          { charId = default; data = default!; return false; } break;
                case (6, 0): if (!TryReadState(payload, ref p, out state))          { charId = default; data = default!; return false; } break;
                case (7, 0): if (!TryReadSceneAreaId(payload, ref p, out _))        { charId = default; data = default!; return false; } break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire))              { charId = default; data = default!; return false; }
                    break;
            }
        }

        charId = cid;
        data = new PartyMemberFastSync(sceneId, position, hp, maxHp, state);
        return true;
    }

    private static bool TryReadCharId(ReadOnlySpan<byte> payload, ref int p, out long charId)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { charId = 0; return false; }
        charId = (long)v;
        return true;
    }

    private static bool TryReadSceneId(ReadOnlySpan<byte> payload, ref int p, out int sceneId)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { sceneId = 0; return false; }
        sceneId = (int)v;
        return true;
    }

    private static bool TryReadPosition(ReadOnlySpan<byte> payload, ref int p, out Position3D position)
    {
        if (!WireProtocol.TryReadLengthDelimited(payload, ref p, out var posBytes)) { position = Position3D.Zero; return false; }
        if (!PositionReader.TryRead(posBytes, out position)) { position = Position3D.Zero; return false; }
        return true;
    }

    private static bool TryReadHp(ReadOnlySpan<byte> payload, ref int p, out long hp)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { hp = 0; return false; }
        hp = (long)v;
        return true;
    }

    private static bool TryReadMaxHp(ReadOnlySpan<byte> payload, ref int p, out long maxHp)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { maxHp = 0; return false; }
        maxHp = (long)v;
        return true;
    }

    private static bool TryReadState(ReadOnlySpan<byte> payload, ref int p, out int state)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { state = 0; return false; }
        state = (int)v;
        return true;
    }

    private static bool TryReadSceneAreaId(ReadOnlySpan<byte> payload, ref int p, out int sceneAreaId)
    {
        if (!WireProtocol.TryReadVarint(payload, ref p, out var v)) { sceneAreaId = 0; return false; }
        sceneAreaId = (int)v;
        return true;
    }
}
