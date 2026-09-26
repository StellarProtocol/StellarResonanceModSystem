using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// One <c>appear</c> entity entry surfaced by
/// <see cref="SyncNearEntitiesReader.TryReadAppearAndDisappear"/>. Only the
/// fields the combat probe actually consumes are projected — the uuid (field 1),
/// when present the <c>AttrCollection</c> sub-message (field 3) used to extract
/// <c>AttrName</c> etc., and the entity's FULL buff set from <c>buff_infos</c>
/// (field 7, <c>BuffInfoSync{uuid=1, buff_infos=2 repeated BuffInfo}</c>) — null when
/// field 7 is absent. <c>BuffsUnknown</c> is true when field 7 was present but could not be decoded
/// completely (truncated framing or a malformed <c>BuffInfo</c>): the snapshot is then NOT complete and the
/// consumer must not replace the entity's buffs with it. A field 7 occurring more than once is merged
/// (entries accumulate, unknown is OR-ed). Everything else on the wire Entity (ent_type, temp_attrs,
/// body_part_infos, passive_skill_infos, buff_effect, appear_type, magnetic queue)
/// is skipped.
/// </summary>
internal readonly record struct AppearEntityMsg(
    long Uuid, AttrCollectionMsg? Attrs, IReadOnlyList<ActiveBuff>? Buffs = null, bool BuffsUnknown = false);

/// <summary>
/// One <c>disappear</c> entity entry. <see cref="DisappearType"/> is the raw
/// <c>zproto.EDisappearType</c> wire int (field 2 on <c>DisappearEntity</c>) — 0 when the field was
/// absent from the bytes, which is proto3's own default-value elision and is INDISTINGUISHABLE from
/// an explicit 0 on the wire (both mean <c>EDisappearNormal</c>). Callers map this to
/// <see cref="Stellar.Abstractions.Domain.EntityDisappearReason"/> (2026-08-26 raid-bosshp-capture-design).
/// </summary>
internal readonly record struct DisappearEntityMsg(long Uuid, int DisappearType);

/// <summary>
/// Pure parser for <c>SyncNearEntities</c> — the message that tells the client
/// which entities entered or left its AOI.
///
/// Schema:
/// <code>
///   message SyncNearEntities {
///     repeated Entity          appear    = 1;
///     repeated DisappearEntity disappear = 2;
///   }
///   message Entity {
///     int64 uuid                 = 1;
///     EEntityType ent_type        = 2;
///     AttrCollection attrs        = 3;
///     TempAttrCollection temp_attrs = 4;
///     ActorBodyPartInfos body_part_infos = 5;
///     SeqPassiveSkillInfo passive_skill_infos = 6;
///     BuffInfoSync buff_infos     = 7;
///     BuffEffectSync buff_effect  = 8;
///     EAppearType appear_type     = 9;
///     map&lt;int32, MagneticQueueAppearInfo&gt; magnetic_ride_queue_change_info_dict = 10;
///   }
///   message DisappearEntity {
///     int64 uuid = 1;
///     EDisappearType type = 2;   // EdisappearNormal=0 (default, elided on the wire) / Dead=1 / Destroy=2 /
///                                // TransferLeave=3 / TransferPassLineLeave=4 (Csharp.cs:60862-61039)
///   }
/// </code>
///
/// Both top-level helpers (<see cref="TryReadDisappearedUuids"/> and
/// <see cref="TryReadAppearAndDisappear"/>) silently swallow malformed
/// inner sub-messages (returning the list collected so far) — better to drop
/// one cache entry than to lose the whole list.
/// </summary>
internal static class SyncNearEntitiesReader
{
    public static bool TryReadDisappearedUuids(ReadOnlySpan<byte> payload, out IReadOnlyList<long> uuids)
    {
        var list = new List<long>(2);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire))
            {
                uuids = Array.Empty<long>();
                return false;
            }
            switch ((field, wire))
            {
                case (2, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var de))
                    {
                        uuids = Array.Empty<long>();
                        return false;
                    }
                    if (TryReadDisappearUuid(de, out var u)) list.Add(u);
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire))
                    {
                        uuids = Array.Empty<long>();
                        return false;
                    }
                    break;
            }
        }
        uuids = list;
        return true;
    }

    /// <summary>
    /// Parse both <c>appear</c> (field 1, repeated Entity) and <c>disappear</c>
    /// (field 2, repeated DisappearEntity) at once. Inside each appear Entity we
    /// extract the uuid (field 1), the <c>AttrCollection</c> sub-message
    /// (field 3, via <see cref="AttrCollectionReader.TryRead"/>) and the buff set
    /// (field 7, via <see cref="BuffInfoReader"/>); every other Entity field is skipped. A malformed inner sub-message is silently
    /// dropped — top-level returns true with whatever was successfully parsed.
    /// </summary>
    public static bool TryReadAppearAndDisappear(
        ReadOnlySpan<byte> payload,
        out IReadOnlyList<AppearEntityMsg> appears,
        out IReadOnlyList<DisappearEntityMsg> disappears)
    {
        var appearList    = new List<AppearEntityMsg>(2);
        var disappearList = new List<DisappearEntityMsg>(2);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return Fail(out appears, out disappears);
            switch ((field, wire))
            {
                case (1, 2): if (!ReadAppearList(payload, ref pos, appearList))     return Fail(out appears, out disappears); break;
                case (2, 2): if (!ReadDisappearList(payload, ref pos, disappearList)) return Fail(out appears, out disappears); break;
                default:     if (!WireProtocol.SkipField(payload, ref pos, wire))    return Fail(out appears, out disappears); break;
            }
        }
        appears   = appearList;
        disappears = disappearList;
        return true;

        static bool Fail(out IReadOnlyList<AppearEntityMsg> a, out IReadOnlyList<DisappearEntityMsg> d)
        {
            a = Array.Empty<AppearEntityMsg>();
            d = Array.Empty<DisappearEntityMsg>();
            return false;
        }
    }

    private static bool ReadAppearList(ReadOnlySpan<byte> payload, ref int pos, List<AppearEntityMsg> appearList)
    {
        if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var ae)) return false;
        if (TryReadEntity(ae, out var appearEntity)) appearList.Add(appearEntity);
        return true;
    }

    private static bool ReadDisappearList(ReadOnlySpan<byte> payload, ref int pos, List<DisappearEntityMsg> disappearList)
    {
        if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var de)) return false;
        if (TryReadDisappearEntity(de, out var entity)) disappearList.Add(entity);
        return true;
    }

    /// <summary>Parse one wire <c>DisappearEntity</c> (uuid field 1 + <c>EDisappearType</c> field 2, varint).
    /// <see cref="DisappearEntityMsg.DisappearType"/> defaults to 0 (<c>EdisappearNormal</c>) when field 2 is
    /// absent — matching proto3's own default-value elision (the writer never emits the tag for the zero
    /// value; see <c>DisappearEntity.WriteTo</c>, Csharp.cs:60974-60990). Returns false only when the uuid
    /// itself can't be read (mirrors <see cref="TryReadDisappearUuid"/>'s contract).</summary>
    private static bool TryReadDisappearEntity(ReadOnlySpan<byte> payload, out DisappearEntityMsg entity)
    {
        long uuid = 0;
        int type = 0;
        bool haveUuid = false;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { entity = default; return false; }
            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref pos, out var u)) { entity = default; return false; }
                    uuid = (long)u;
                    haveUuid = true;
                    break;
                case (2, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref pos, out var t)) { entity = default; return false; }
                    type = (int)t;
                    break;
                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) { entity = default; return false; }
                    break;
            }
        }
        if (!haveUuid) { entity = default; return false; }
        entity = new DisappearEntityMsg(uuid, type);
        return true;
    }

    /// <summary>Parse one wire <c>Entity</c> (uuid field 1 + AttrCollection field 3 + BuffInfoSync field 7) —
    /// used for both AOI appears and EnterScene's PlayerEnt. Other Entity fields are skipped.</summary>
    internal static bool TryReadEntity(ReadOnlySpan<byte> payload, out AppearEntityMsg entity)
    {
        entity = default;
        long uuid = 0;
        AttrCollectionMsg? attrs = null;
        List<ActiveBuff>? buffs = null;
        bool buffsUnknown = false;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return false;
            switch ((field, wire))
            {
                case (1, 0):
                    if (!WireProtocol.TryReadVarint(payload, ref pos, out var u)) return false;
                    uuid = (long)u;
                    break;

                case (3, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var attrBytes)) return false;
                    // Silently drop a malformed AttrCollection — we still want
                    // the uuid surfaced so the caller can register the entity.
                    // ToArray: one copy per collection (memory-based reader; appear bursts are bounded).
                    if (AttrCollectionReader.TryRead(attrBytes.ToArray(), out var ac)) attrs = ac;
                    break;

                case (7, 2):
                    if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var buffSync)) return false;
                    MergeBuffInfoSync(buffSync, ref buffs, ref buffsUnknown);
                    break;

                default:
                    if (!WireProtocol.SkipField(payload, ref pos, wire)) return false;
                    break;
            }
        }
        entity = new AppearEntityMsg(uuid, attrs, buffs, buffsUnknown);
        return true;
    }

    /// <summary>Protobuf merge semantics for a repeated occurrence of the embedded field 7: the repeated
    /// <c>buff_infos</c> of every occurrence accumulate, and one undecodable occurrence marks the whole set
    /// unknown (a later good occurrence never clears the flag).</summary>
    private static void MergeBuffInfoSync(ReadOnlySpan<byte> payload, ref List<ActiveBuff>? buffs, ref bool unknown)
    {
        var part = ReadBuffInfoSync(payload, out var partUnknown);
        unknown |= partUnknown;
        if (part is null) return;
        if (buffs is null) buffs = part;
        else buffs.AddRange(part);
    }

    /// <summary>Decode <c>BuffInfoSync{uuid=1, buff_infos=2 repeated BuffInfo}</c> with the shared
    /// <see cref="BuffInfoReader"/>. All-or-nothing: any framing or <c>BuffInfo</c> failure sets
    /// <paramref name="unknown"/> and returns null — a partial list must never pass for the full set. The list
    /// is sized exactly by a framing-only pre-count pass (no per-buff decoding, no regrowth).</summary>
    private static List<ActiveBuff>? ReadBuffInfoSync(ReadOnlySpan<byte> payload, out bool unknown)
    {
        unknown = true;
        int count = CountBuffInfos(payload);
        if (count < 0) return null;
        var list = new List<ActiveBuff>(count);
        int pos = 0;
        while (pos < payload.Length)
        {
            WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire);
            if (field == 2 && wire == 2)
            {
                WireProtocol.TryReadLengthDelimited(payload, ref pos, out var bi);
                if (!BuffInfoReader.TryRead(bi, out var buff)) return null;
                list.Add(buff);
            }
            else WireProtocol.SkipField(payload, ref pos, wire);
        }
        unknown = false;
        return list;
    }

    // Framing-only pass: number of buff_infos entries, or -1 when the frame is malformed/truncated.
    private static int CountBuffInfos(ReadOnlySpan<byte> payload)
    {
        int count = 0, pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return -1;
            if (field == 2 && wire == 2) count++;
            if (!WireProtocol.SkipField(payload, ref pos, wire)) return -1;
        }
        return count;
    }

    private static bool TryReadDisappearUuid(ReadOnlySpan<byte> payload, out long uuid)
    {
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) { uuid = 0; return false; }
            if (field == 1 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(payload, ref pos, out var u)) { uuid = 0; return false; }
                uuid = (long)u;
                return true;
            }
            if (!WireProtocol.SkipField(payload, ref pos, wire)) { uuid = 0; return false; }
        }
        uuid = 0;
        return false;
    }
}
