using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Result of parsing AoiSyncDelta field 10 (BuffEffectSync) for one entity this
/// delta. <see cref="Upserts"/> are added/refreshed buffs (keyed by
/// <c>ActiveBuff.BuffUuid</c>); <see cref="Removes"/> are removed BuffUuids.
/// <see cref="EffectIds"/> are the buff effect-id changes in wire order (null when none) — independent of
/// <see cref="Touched"/>: a Customize-only effect changes ids with no upsert.
/// </summary>
internal readonly record struct BuffEventBatch(
    bool Touched,
    IReadOnlyList<ActiveBuff> Upserts,
    IReadOnlyList<int> Removes,
    IReadOnlyList<BuffEffectTrace>? Trace = null,
    IReadOnlyList<BuffEffectIdUpdate>? EffectIds = null)
{
    public static BuffEventBatch None { get; } =
        new(false, Array.Empty<ActiveBuff>(), Array.Empty<int>());
}

/// <summary>
/// Diagnostics-only raw view of one <c>BuffEffect</c>: event type, the effect's BuffUuid, the payload kind
/// (18 AddBuff, 19 BuffChange, 0 none) and the payload's uuid/layer/duration/create time as parsed.
/// </summary>
internal readonly record struct BuffEffectTrace(
    int Type, int BuffUuid, int PayloadKind, int PayloadUuid, int Layer, int DurationMs, long CreateTimeMs);

/// <summary>
/// Pure parser for the <c>BuffEffectSync</c> sub-message carried in
/// <c>AoiSyncDelta</c> field 10 (event stream — confirmed via ZDPS BPSR-ZDPS +
/// Stellar hex-dump recon):
/// <code>
///   BuffEffectSync { int64 Uuid=1; repeated BuffEffect BuffEffects=2; }
///   BuffEffect { EBuffEventType Type=1; int BuffUuid=2; int64 HostUuid=3;
///                int64 TriggerTime=4; repeated BuffEffectLogicInfo LogicEffect=5; }
///   BuffEffectLogicInfo { EBuffEffectLogicPbType EffectType=1; bytes RawData=2; }
///     EffectType==0  (PlayEffect) → RawData = BuffEffectLogicPlayEffect { effect_id=1 }
///     EffectType==18 (AddBuff)    → RawData = BuffInfo   (BaseId etc.)
///     EffectType==19 (BuffChange) → RawData = BuffChange (layer/dur/create)
/// </code>
/// Per BuffEffect: <c>Type == Remove(2)</c>, or a <c>RemoveLayer(6)</c> that leaves
/// no layers (no buff payload, or a payload whose Layer is 0 / absent — the
/// consumable's final expiry) → remove BuffUuid; else (incl. RemoveLayer +
/// BuffChange with layers left, the potion/food re-eat time extension) upsert the
/// resolved <see cref="ActiveBuff"/> (with BuffUuid set). Never throws.
/// <c>trace: true</c> (diagnostics) also returns every effect raw in
/// <see cref="BuffEventBatch.Trace"/>.
/// <para>Effect ids (resonance-logs-cn decoder.rs <c>decode_buff_effect_sync</c>), per BuffEffect in this order,
/// each non-empty set replacing the previous: the AddBuff payload's own <c>BuffInfo.LogicEffect</c> PlayEffect ids;
/// the effect's PlayEffect ids when it carries an AddBuff/BuffChange entry; a <c>BuffEventCustomize(1002)</c>'s
/// customize values (every non-StopAll entry). A removal (as above) drops them. Host = <c>BuffEffect.HostUuid</c>,
/// else <c>BuffEffectSync.Uuid</c>, else 0 (the sink uses the delta's entity).</para>
/// </summary>
internal static class BuffEffectSyncReader
{
    private const int LogicAddBuff = BuffEffectLogicReader.LogicAddBuff;
    private const int LogicBuffChange = BuffEffectLogicReader.LogicBuffChange;
    private const int EventRemove = 2;
    private const int EventRemoveLayer = 6;
    private const int EventCustomize = 1002;

    // Per-BuffEffect effect-id state: the effect's own header + what its logic entries carried.
    private ref struct EffectIdScratch
    {
        public int Type, BuffUuid;
        public long Host;
        public bool AddOrChange;
        public IdBuffer Play, Custom;
        public int[]? InnerIds;
        public long InnerHost;
        public int InnerBuff;
    }

    public static BuffEventBatch TryRead(ReadOnlySpan<byte> payload, bool trace = false)
    {
        var upserts = new List<ActiveBuff>(2);
        var removes = new List<int>(1);
        var traces = trace ? new List<BuffEffectTrace>(2) : null;
        List<BuffEffectIdUpdate>? ids = null;
        long syncUuid = 0;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return BuffEventBatch.None;
            if (field == 1 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(payload, ref pos, out var su)) return BuffEventBatch.None;
                syncUuid = (long)su;
            }
            else if (field == 2 && wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var be)) return BuffEventBatch.None;
                if (!ApplyBuffEffect(be, upserts, removes, traces, ref ids)) return BuffEventBatch.None;
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) return BuffEventBatch.None;
        }
        if (ids != null && syncUuid != 0) FillHost(ids, syncUuid);
        return new BuffEventBatch(upserts.Count > 0 || removes.Count > 0, upserts, removes, traces, ids);
    }

    private static bool ApplyBuffEffect(ReadOnlySpan<byte> payload, List<ActiveBuff> upserts, List<int> removes,
        List<BuffEffectTrace>? traces, ref List<BuffEffectIdUpdate>? ids)
    {
        int payloadKind = 0;
        ActiveBuff? info = null;
        var fx = new EffectIdScratch { Play = new IdBuffer(stackalloc int[8]), Custom = new IdBuffer(stackalloc int[8]) };
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return false;
            switch ((field, wire))
            {
                case (1, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var t)) return false; fx.Type = (int)t; break;
                case (2, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var u)) return false; fx.BuffUuid = (int)u; break;
                case (3, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var h)) return false; fx.Host = (long)h; break;
                case (5, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var le)) return false;
                    if (TryReadLogic(le, out var parsed, out var kind, ref fx)) { info = parsed; payloadKind = kind; }   // last wins
                    break;
                default: if (!WireProtocol.SkipField(payload, ref pos, wire)) return false; break;
            }
        }
        int type = fx.Type, buffUuid = fx.BuffUuid;

        traces?.Add(info is { } tr
            ? new BuffEffectTrace(type, buffUuid, payloadKind, tr.BuffUuid, tr.Layer, tr.DurationMs, tr.CreateTimeMs)
            : new BuffEffectTrace(type, buffUuid, 0, 0, 0, 0, 0));

        // RemoveLayer that still leaves layers is an UPDATE: re-eating a potion/food keeps the same uuid and
        // announces the extended time as RemoveLayer + BuffChange (owner 2026-10-05; ZDPS reads it the same way).
        // The consumable's final expiry is ALSO a payload-carrying RemoveLayer, with no layers left (Layer 0 /
        // absent) — that, Remove, and a bare RemoveLayer with nothing to update all drop the buff.
        bool removeLayerEnds = type == EventRemoveLayer && (info is null || info.Value.Layer <= 0);
        bool removed = type == EventRemove || removeLayerEnds;
        EmitEffectIds(ref fx, removed, ref ids);
        if (removed)
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
    // logic types (gravity, zoom, …) return false (skipped). Every entry also feeds the effect-id scratch.
    private static bool TryReadLogic(ReadOnlySpan<byte> payload, out ActiveBuff buff, out int effectKind,
        ref EffectIdScratch fx)
    {
        buff = default; effectKind = 0;
        if (!BuffEffectLogicReader.TryReadEntry(payload, out var effectType, out var raw, out var hasRaw)) return false;
        effectKind = effectType;
        NoteEffectIds(effectType, raw, ref fx);
        if (!hasRaw) return false;
        if (effectType == LogicAddBuff)
        {
            if (!BuffInfoReader.TryRead(raw, out buff, out var innerHost, out var innerIds)) return false;
            if (innerIds != null) { fx.InnerIds = innerIds; fx.InnerHost = innerHost; fx.InnerBuff = buff.BuffUuid; }
            return true;
        }
        if (effectType == LogicBuffChange)
        {
            if (!BuffChangeReader.TryRead(raw, out var layer, out var dur, out var create)) return false;
            buff = new ActiveBuff(0, 0, 0, EntityId.None, 0, layer, create, dur);
            return true;
        }
        return false;
    }

    // An absent RawData reads as empty bytes, like the game's own ByteString (BuffEffectLogicReader remarks).
    private static void NoteEffectIds(int effectType, ReadOnlySpan<byte> raw, ref EffectIdScratch fx)
    {
        if (effectType == LogicAddBuff || effectType == LogicBuffChange) fx.AddOrChange = true;
        if (effectType == BuffEffectLogicReader.LogicPlayEffect && BuffEffectLogicReader.TryDecodePlayEffectId(raw, out int id))
            fx.Play.Add(id);
        if (effectType != BuffEffectLogicReader.LogicStopAll && BuffEffectLogicReader.TryDecodeCustomizeValue(raw, out int v))
            fx.Custom.Add(v);
    }

    // Effect-id updates for one BuffEffect, in the decoder.rs order (later replaces earlier). Arrays and the list are
    // allocated only when ids exist; the common no-id effect allocates nothing.
    private static void EmitEffectIds(ref EffectIdScratch fx, bool removed, ref List<BuffEffectIdUpdate>? ids)
    {
        if (removed)
        {
            (ids ??= new List<BuffEffectIdUpdate>(1)).Add(new BuffEffectIdUpdate(fx.Host, fx.BuffUuid, null));
            return;
        }
        if (fx.InnerIds != null)
            (ids ??= new List<BuffEffectIdUpdate>(1)).Add(new BuffEffectIdUpdate(
                fx.InnerHost != 0 ? fx.InnerHost : fx.Host, fx.InnerBuff != 0 ? fx.InnerBuff : fx.BuffUuid, fx.InnerIds));
        if (fx.AddOrChange && fx.Play.ToArrayOrNull() is { } play)
            (ids ??= new List<BuffEffectIdUpdate>(1)).Add(new BuffEffectIdUpdate(fx.Host, fx.BuffUuid, play));
        if (fx.Type == EventCustomize && fx.Custom.ToArrayOrNull() is { } custom)
            (ids ??= new List<BuffEffectIdUpdate>(1)).Add(new BuffEffectIdUpdate(fx.Host, fx.BuffUuid, custom));
    }

    // BuffEffectSync.Uuid is the fallback host for effects that named none.
    private static void FillHost(List<BuffEffectIdUpdate> ids, long syncUuid)
    {
        for (int i = 0; i < ids.Count; i++)
            if (ids[i].HostUuid == 0) ids[i] = ids[i] with { HostUuid = syncUuid };
    }
}
