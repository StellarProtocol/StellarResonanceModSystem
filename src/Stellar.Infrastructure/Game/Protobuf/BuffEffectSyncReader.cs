using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Result of parsing AoiSyncDelta field 10 (BuffEffectSync) for one entity this
/// delta. <see cref="Upserts"/> are added/refreshed buffs (keyed by
/// <c>ActiveBuff.BuffUuid</c>); <see cref="Removes"/> are removed BuffUuids.
/// </summary>
internal readonly record struct BuffEventBatch(
    bool Touched,
    IReadOnlyList<ActiveBuff> Upserts,
    IReadOnlyList<int> Removes)
{
    public static BuffEventBatch None { get; } =
        new(false, Array.Empty<ActiveBuff>(), Array.Empty<int>());
}

/// <summary>
/// Pure parser for the <c>BuffEffectSync</c> sub-message carried in
/// <c>AoiSyncDelta</c> field 10 (event stream — confirmed via ZDPS BPSR-ZDPS +
/// Stellar hex-dump recon):
/// <code>
///   BuffEffectSync { int64 Uuid=1; repeated BuffEffect BuffEffects=2; }
///   BuffEffect { EBuffEventType Type=1; int BuffUuid=2; int64 HostUuid=3;
///                int64 TriggerTime=4; repeated BuffEffectLogicInfo LogicEffect=5; }
///   BuffEffectLogicInfo { EBuffEffectLogicPbType EffectType=1; bytes RawData=2; }
///     EffectType==18 (AddBuff)    → RawData = BuffInfo   (BaseId etc.)
///     EffectType==19 (BuffChange) → RawData = BuffChange (layer/dur/create)
/// </code>
/// Per BuffEffect: <c>Type ∈ {Remove(2), RemoveLayer(6)}</c> → remove BuffUuid;
/// else upsert the resolved <see cref="ActiveBuff"/> (with BuffUuid set). Never
/// throws.
/// </summary>
internal static class BuffEffectSyncReader
{
    private const int LogicAddBuff = 18;
    private const int LogicBuffChange = 19;
    private const int EventRemove = 2;
    private const int EventRemoveLayer = 6;

    public static BuffEventBatch TryRead(ReadOnlySpan<byte> payload)
    {
        var upserts = new List<ActiveBuff>(2);
        var removes = new List<int>(1);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return BuffEventBatch.None;
            if (field == 2 && wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var be)) return BuffEventBatch.None;
                if (!ApplyBuffEffect(be, upserts, removes)) return BuffEventBatch.None;
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) return BuffEventBatch.None;
        }
        return new BuffEventBatch(upserts.Count > 0 || removes.Count > 0, upserts, removes);
    }

    private static bool ApplyBuffEffect(ReadOnlySpan<byte> payload, List<ActiveBuff> upserts, List<int> removes)
    {
        int type = 0, buffUuid = 0;
        ActiveBuff? info = null;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return false;
            switch ((field, wire))
            {
                case (1, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var t)) return false; type = (int)t; break;
                case (2, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var u)) return false; buffUuid = (int)u; break;
                case (5, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var le)) return false;
                    if (TryReadLogic(le, out var parsed)) info = parsed;   // AddBuff/BuffChange; last wins
                    break;
                default: if (!WireProtocol.SkipField(payload, ref pos, wire)) return false; break;
            }
        }

        if (type == EventRemove || type == EventRemoveLayer)
        {
            removes.Add(buffUuid);
            return true;
        }
        if (info is { } b)
            upserts.Add(b.BuffUuid != 0 ? b : b with { BuffUuid = buffUuid });
        return true;
    }

    // Parses one BuffEffectLogicInfo. Returns true + the resolved ActiveBuff when
    // it carries a buff payload (AddBuff → full; BuffChange → partial). Other
    // logic types (gravity, zoom, …) return false (skipped).
    private static bool TryReadLogic(ReadOnlySpan<byte> payload, out ActiveBuff buff)
    {
        buff = default;
        int effectType = 0;
        ReadOnlySpan<byte> raw = default;
        bool hasRaw = false;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return false;
            switch ((field, wire))
            {
                case (1, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var et)) return false; effectType = (int)et; break;
                case (2, 2): if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out raw)) return false; hasRaw = true; break;
                default: if (!WireProtocol.SkipField(payload, ref pos, wire)) return false; break;
            }
        }
        if (!hasRaw) return false;
        if (effectType == LogicAddBuff)
            return BuffInfoReader.TryRead(raw, out buff);
        if (effectType == LogicBuffChange)
        {
            if (!BuffChangeReader.TryRead(raw, out var layer, out var dur, out var create)) return false;
            buff = new ActiveBuff(0, 0, 0, EntityId.None, 0, layer, create, dur);
            return true;
        }
        return false;
    }
}
