using System;
using Stellar.Abstractions.Domain;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Reads <c>zproto.Position</c> messages — three fixed32 floats at tags 1 (x),
/// 2 (y), 3 (z). Used by <see cref="TeamMemberFastSyncDataReader"/>.
/// </summary>
internal static class PositionReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out Position3D pos)
    {
        float x = 0f, y = 0f, z = 0f;
        int p = 0;

        while (p < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref p, out var field, out var wire))
            { pos = default; return false; }

            switch ((field, wire))
            {
                case (1, 5):
                    if (p + 4 > payload.Length) { pos = default; return false; }
                    x = BitConverter.ToSingle(payload.Slice(p, 4));
                    p += 4;
                    break;
                case (2, 5):
                    if (p + 4 > payload.Length) { pos = default; return false; }
                    y = BitConverter.ToSingle(payload.Slice(p, 4));
                    p += 4;
                    break;
                case (3, 5):
                    if (p + 4 > payload.Length) { pos = default; return false; }
                    z = BitConverter.ToSingle(payload.Slice(p, 4));
                    p += 4;
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref p, wire)) { pos = default; return false; }
                    break;
            }
        }

        pos = new Position3D(x, y, z);
        return true;
    }
}
