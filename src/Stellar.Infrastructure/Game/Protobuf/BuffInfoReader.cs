using System;
using Stellar.Abstractions.Domain;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Pure parser for a single <c>BuffInfo</c> protobuf message — the payload of a
/// <c>BuffEffectLogicInfo.RawData</c> when its EffectType is BuffEffectAddBuff(18).
/// Field numbers (stru_buff_info.proto): 1=BuffUuid, 2=BaseId, 3=Level,
/// 6=CreateTime (epoch ms), 7=FireUuid, 8=Layer, 10=Count, 11=Duration (ms).
/// Never throws.
/// </summary>
internal static class BuffInfoReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out ActiveBuff buff)
    {
        int buffUuid = 0, baseId = 0, level = 0, layer = 0, count = 0, dur = 0;
        long createMs = 0, firer = 0;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { buff = default; return false; }
            switch ((field, wire))
            {
                case (1, 0):  if (!WireProtocol.TryReadVarint(payload, ref pos, out var v1))  { buff = default; return false; } buffUuid = (int)v1;  break;
                case (2, 0):  if (!WireProtocol.TryReadVarint(payload, ref pos, out var v2))  { buff = default; return false; } baseId   = (int)v2;  break;
                case (3, 0):  if (!WireProtocol.TryReadVarint(payload, ref pos, out var v3))  { buff = default; return false; } level    = (int)v3;  break;
                case (6, 0):  if (!WireProtocol.TryReadVarint(payload, ref pos, out var v6))  { buff = default; return false; } createMs = (long)v6; break;
                case (7, 0):  if (!WireProtocol.TryReadVarint(payload, ref pos, out var v7))  { buff = default; return false; } firer    = (long)v7; break;
                case (8, 0):  if (!WireProtocol.TryReadVarint(payload, ref pos, out var v8))  { buff = default; return false; } layer    = (int)v8;  break;
                case (10, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v10)) { buff = default; return false; } count    = (int)v10; break;
                case (11, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var v11)) { buff = default; return false; } dur      = (int)v11; break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) { buff = default; return false; }
                    break;
            }
        }
        buff = new ActiveBuff(buffUuid, baseId, level, new EntityId(firer), count, layer, createMs, dur);
        return true;
    }
}
