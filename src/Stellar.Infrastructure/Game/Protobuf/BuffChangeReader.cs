using System;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Pure parser for a <c>BuffChange</c> protobuf message — the payload of a
/// <c>BuffEffectLogicInfo.RawData</c> when its EffectType is
/// BuffEffectBuffChange(19). Fields: 1=Layer, 2=Duration(ms), 3=CreateTime(epoch
/// ms). Carries no BaseId. Never throws.
/// </summary>
internal static class BuffChangeReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out int layer, out int duration, out long createTime)
    {
        layer = 0; duration = 0; createTime = 0;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return false;
            switch ((field, wire))
            {
                case (1, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v1)) return false; layer      = (int)v1;  break;
                case (2, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v2)) return false; duration   = (int)v2;  break;
                case (3, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v3)) return false; createTime = (long)v3; break;
                default:     if (!WireProtocol.SkipField(payload, ref pos, wire)) return false; break;
            }
        }
        return true;
    }
}
