using System;
using System.Collections.Generic;
using Stellar.Wire;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Pure parser for the <c>SkillEffect</c> wrapper carried in
/// <c>AoiSyncDelta</c> field 7:
/// <code>
///   message SkillEffect {
///     optional int64 Uuid    = 1;        // ignored in v1
///     repeated SyncDamageInfo Damages = 2;
///   }
/// </code>
/// Returns only the parsed <c>Damages</c> list — the wrapper's
/// <c>Uuid</c> field is not exposed because the v1 DPS surface uses the
/// per-<c>SyncDamageInfo</c> <c>AttackerUuid</c> / <c>TopSummonerId</c>
/// for caster attribution, not the wrapper UUID.
///
/// Defensive Try* pattern matching the other readers — malformed bytes
/// cause a short-circuit return, never an exception.
/// </summary>
internal static class SkillEffectReader
{
    public static bool TryRead(ReadOnlySpan<byte> payload, out IReadOnlyList<SyncDamageInfoMsg> damages)
    {
        var list = new List<SyncDamageInfoMsg>(2);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire))
            {
                damages = Array.Empty<SyncDamageInfoMsg>();
                return false;
            }
            switch ((field, wire))
            {
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var bytes))
                    {
                        damages = Array.Empty<SyncDamageInfoMsg>();
                        return false;
                    }
                    if (!SyncDamageInfoReader.TryRead(bytes, out var dmg))
                    {
                        damages = Array.Empty<SyncDamageInfoMsg>();
                        return false;
                    }
                    list.Add(dmg);
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire))
                    {
                        damages = Array.Empty<SyncDamageInfoMsg>();
                        return false;
                    }
                    break;
            }
        }
        damages = list;
        return true;
    }
}
